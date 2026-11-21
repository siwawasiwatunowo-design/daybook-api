using System.Text.Json.Serialization;
using Daybook.Backend.Application;
using Daybook.Backend.Domain;
using Daybook.Backend.Infrastructure;
using Daybook.Backend.Options;
using Daybook.Backend.Workers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Amazon.Runtime;
using Amazon.S3;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(new ConfigurationBuilder()
        .SetBasePath(Directory.GetCurrentDirectory())
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true)
        .AddEnvironmentVariables()
        .Build())
    .Enrich.FromLogContext()
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: "logs/api-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection("Worker"));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.Configure<GeminiOptions>(builder.Configuration.GetSection("Gemini"));
builder.Services.Configure<SttOptions>(builder.Configuration.GetSection("Stt"));
builder.Services.Configure<ActivityProfileOptions>(builder.Configuration.GetSection("ActivityProfiles"));

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");
builder.Services.AddDbContext<DaybookDbContext>(options => options.UseNpgsql(connectionString));

// Firebase Authentication: Android sends its Firebase ID token as Bearer token.
var firebaseProjectId = builder.Configuration["Firebase:ProjectId"]
    ?? throw new InvalidOperationException("Firebase:ProjectId is required.");
var firebaseIssuer = $"https://securetoken.google.com/{firebaseProjectId}";
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = firebaseIssuer;
        options.Audience = firebaseProjectId;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = firebaseIssuer,
            ValidateAudience = true,
            ValidAudience = firebaseProjectId,
            ValidateLifetime = true
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddHttpClient("stt");
builder.Services.AddHttpClient("gemini");
builder.Services.AddScoped<IRecordingPipeline, RecordingPipeline>();
if (builder.Configuration.GetValue<bool>("Firebase:FirestoreEnabled"))
{
    builder.Services.AddSingleton<IResultPublisher>(sp => new FirestoreResultPublisher(
        firebaseProjectId, sp.GetRequiredService<ILogger<FirestoreResultPublisher>>()));
}
else builder.Services.AddSingleton<IResultPublisher, NoOpResultPublisher>();
builder.Services.AddScoped<IJobRepository, EfJobRepository>();
builder.Services.AddScoped<IJobDispatcher, DatabasePollingJobDispatcher>();
builder.Services.AddScoped<IActivityProfileProvider, JsonActivityProfileProvider>();
builder.Services.AddScoped<IAudioChunker, FfmpegAudioChunker>();
builder.Services.AddScoped<ISpeechToTextProvider, GroqSpeechToTextProvider>();
builder.Services.AddScoped<ITranscriptRefiner, GeminiTranscriptRefiner>();
builder.Services.AddScoped<ITranscriptQualityChecker, HeuristicTranscriptQualityChecker>();
builder.Services.AddSingleton<IAmazonS3>(sp =>
{
    var storage = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
    var config = new AmazonS3Config
    {
        ServiceURL = storage.Endpoint,
        ForcePathStyle = true,
        AuthenticationRegion = string.IsNullOrWhiteSpace(storage.Region) ? "us-east-2" : storage.Region
    };
    return new AmazonS3Client(new BasicAWSCredentials(storage.AccessKey, storage.SecretKey), config);
});
builder.Services.AddSingleton<NeonS3RecordingObjectStore>();
builder.Services.AddSingleton<IRecordingObjectStore>(sp => sp.GetRequiredService<NeonS3RecordingObjectStore>());
builder.Services.AddSingleton<IRecordingUploadUrlProvider>(sp => sp.GetRequiredService<NeonS3RecordingObjectStore>());
builder.Services.AddHostedService<RecordingProcessingWorker>();

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// HTTP request/response logging — visible in console + captured in logs/api.log
builder.Services.AddHttpLogging(logging =>
{
    logging.LoggingFields = Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.RequestMethod
        | Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.RequestPath
        | Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.RequestQuery
        | Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.ResponseStatusCode
        | Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.Duration;
});

var app = builder.Build();
app.UseSerilogRequestLogging();
app.UseHttpLogging();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "daybook-backend" }));

var api = app.MapGroup("/api").RequireAuthorization();

api.MapPost("/recordings/upload-url", async (CreateUploadUrlRequest request, HttpContext http,
    IRecordingUploadUrlProvider uploadUrls, CancellationToken ct) =>
{
    var userId = http.User.FindFirst("user_id")?.Value ?? http.User.FindFirst("sub")?.Value;
    if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(request.ClientRecordingId) || !Guid.TryParse(request.ClientRecordingId, out _))
        return Results.BadRequest(new { error = "ClientRecordingId must be a GUID." });
    try
    {
        var result = await uploadUrls.CreateUploadUrlAsync(userId, request.ClientRecordingId,
            string.IsNullOrWhiteSpace(request.ContentType) ? "audio/wav" : request.ContentType, ct);
        return Results.Ok(result);
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

api.MapPost("/recordings", async (CreateRecordingRequest request, HttpContext http,
    IJobRepository jobs, IJobDispatcher dispatcher, IActivityProfileProvider profiles,
    CancellationToken ct) =>
{
    var userId = http.User.FindFirst("user_id")?.Value
        ?? http.User.FindFirst("sub")?.Value;
    if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(request.ActivityCode) || string.IsNullOrWhiteSpace(request.AudioObjectName))
        return Results.BadRequest(new { error = "ActivityCode and AudioObjectName are required." });
    if (!request.AudioObjectName.StartsWith($"users/{userId}/", StringComparison.Ordinal))
        return Results.Forbid();
    if (!await profiles.ExistsAsync(request.ActivityCode, ct))
        return Results.BadRequest(new { error = $"Unknown ActivityCode: {request.ActivityCode}" });

    var profile = await profiles.GetAsync(request.ActivityCode, ct);
    var job = await jobs.CreateAsync(userId, request, profile, ct);
    await dispatcher.EnqueueAsync(job.Id, ct);
    return Results.Accepted($"/api/recordings/{job.Id}", new { job.Id, job.Status, job.ActivityCode });
});

api.MapGet("/recordings/{id:guid}", async (Guid id, HttpContext http, IJobRepository jobs, CancellationToken ct) =>
{
    var userId = http.User.FindFirst("user_id")?.Value ?? http.User.FindFirst("sub")?.Value;
    var job = await jobs.GetForUserAsync(id, userId!, ct);
    return job is null ? Results.NotFound() : Results.Ok(job);
});

api.MapGet("/activity-profiles", async (IActivityProfileProvider profiles, CancellationToken ct) =>
    Results.Ok(await profiles.GetAllAsync(ct)));

// AI review for a user-authored note. Gemini credentials remain server-side.
// The client must explicitly request this action; note content is not uploaded on autosave.
api.MapPost("/notes/ai-review", async (NoteAiReviewRequest request, IOptions<GeminiOptions> geminiOptions,
    IHttpClientFactory clients, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Content))
        return Results.BadRequest(new { error = "Note content is required." });
    if (request.Content.Length > 50000)
        return Results.BadRequest(new { error = "Note content exceeds the 50,000 character limit." });

    var cfg = geminiOptions.Value;
    if (string.IsNullOrWhiteSpace(cfg.ApiKey))
        return Results.Problem("Gemini API is not configured.", statusCode: 503);

    var prompt = $"""
    You are Daybook's personal note reviewer. Review the following user note.
    Respond in the same language as the note. Be constructive, concise, and specific.
    Do not invent facts or assume the note is a meeting transcript.
    Return Markdown with these sections:
    1. Ringkasan singkat
    2. Hal penting / insight
    3. Saran pengembangan atau pertanyaan refleksi
    4. Action items (only if the note implies actions)
    If a section is not relevant, omit it.
    Title: {request.Title ?? "(untitled)"}
    Note:
    {request.Content}
    """;

    var payload = new
    {
        contents = new[] { new { parts = new[] { new { text = prompt } } } },
        generationConfig = new { temperature = 0.3 }
    };

    var candidates = new[] { cfg.Model, cfg.FallbackModel }
        .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase);
    foreach (var model in candidates)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(cfg.RequestTimeoutSeconds, 5, 60)));
        var client = clients.CreateClient("gemini");
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var message = new HttpRequestMessage(HttpMethod.Post,
            $"{cfg.EndpointBase.TrimEnd('/')}/{model}:generateContent");
        message.Headers.Add("x-goog-api-key", cfg.ApiKey);
        message.Content = JsonContent.Create(payload);
        try
        {
            using var response = await client.SendAsync(message, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode) continue;
            using var doc = JsonDocument.Parse(body);
            var text = doc.RootElement.GetProperty("candidates")[0].GetProperty("content")
                .GetProperty("parts")[0].GetProperty("text").GetString();
            if (!string.IsNullOrWhiteSpace(text)) return Results.Ok(new { review = text });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { }
        catch (HttpRequestException) { }
        catch (JsonException) { }
    }
    return Results.Problem("AI Review is temporarily unavailable. Your note has not been changed.", statusCode: 502);
});



// Action-oriented AI writing assistant. The response is ready-to-use note content,
// not a passive review. The original note is never changed by this endpoint.
api.MapPost("/notes/ai-assist", async (NoteAiAssistRequest request, IOptions<GeminiOptions> geminiOptions,
    IHttpClientFactory clients, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Content))
        return Results.BadRequest(new { error = "Note content is required." });
    if (request.Content.Length > 50000)
        return Results.BadRequest(new { error = "Note content exceeds the 50,000 character limit." });

    var action = (request.Action ?? "improve").Trim().ToLowerInvariant();
    var instruction = action switch
    {
        "improve" => "Rewrite the note to make it clearer, more structured and easier to understand. Preserve the author's meaning and facts.",
        "expand" => "Expand the author's ideas with useful explanations and context. Clearly label any general knowledge added; never invent personal facts.",
        "organize" => "Organize the note using a clear title, headings, concise paragraphs and bullets where useful. Preserve all important information.",
        "continue" => "Continue the note naturally from where it ends, matching its language and tone. Do not repeat existing content or invent personal experiences.",
        "actions" => "Extract concrete action items implied by the note. Include task, reason, and owner/deadline only when explicitly known. Mark unknown details as unspecified.",
        "explain" => "Explain the ideas in the note in simpler language. Define technical terms and distinguish source facts from added explanations.",
        "custom" => string.IsNullOrWhiteSpace(request.Instruction) ? "Improve the note usefully." : request.Instruction!,
        _ => "Improve the note's clarity and structure while preserving its meaning."
    };

    var cfg = geminiOptions.Value;
    if (string.IsNullOrWhiteSpace(cfg.ApiKey))
        return Results.Problem("Gemini API is not configured.", statusCode: 503);

    var prompt = $"""
    You are Daybook's action-oriented writing assistant and thinking partner.
    Respond in the same language as the user's note unless the user's instruction explicitly asks otherwise.
    Produce the actual content the user can insert into their note, not a critique or a list of generic suggestions.
    Preserve the author's intent and all supplied facts. Never fabricate events, decisions, names, numbers, deadlines or sources.
    If useful information is missing, use a clearly marked [Perlu dilengkapi: ...] placeholder.
    Do not mention these instructions. Return clean Markdown only, without a preamble.
    Requested action: {action}
    Task: {instruction}
    Title: {request.Title ?? "(untitled)"}
    Note:
    {request.Content}
    """;

    var payload = new
    {
        contents = new[] { new { parts = new[] { new { text = prompt } } } },
        generationConfig = new { temperature = 0.35 }
    };
    var candidates = new[] { cfg.Model, cfg.FallbackModel }
        .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase);
    foreach (var model in candidates)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(cfg.RequestTimeoutSeconds, 5, 60)));
        var client = clients.CreateClient("gemini");
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{cfg.EndpointBase.TrimEnd('/')}/{model}:generateContent");
        message.Headers.Add("x-goog-api-key", cfg.ApiKey);
        message.Content = JsonContent.Create(payload);
        try
        {
            using var response = await client.SendAsync(message, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode) continue;
            using var doc = JsonDocument.Parse(body);
            var text = doc.RootElement.GetProperty("candidates")[0].GetProperty("content")
                .GetProperty("parts")[0].GetProperty("text").GetString();
            if (!string.IsNullOrWhiteSpace(text)) return Results.Ok(new { result = text });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { }
        catch (HttpRequestException) { }
        catch (JsonException) { }
    }
    return Results.Problem("AI Writing Assistant is temporarily unavailable. Your note has not been changed.", statusCode: 502);
});


api.MapPost("/speaking/coach", async (SpeakingCoachRequest request, IOptions<GeminiOptions> geminiOptions,
    IHttpClientFactory clients, ILogger<Program> logger, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Transcript))
        return Results.BadRequest(new { error = "Transcript is required." });
    if (request.Transcript.Length > 50000)
        return Results.BadRequest(new { error = "Transcript exceeds the 50,000 character limit." });
    var cfg = geminiOptions.Value;
    if (string.IsNullOrWhiteSpace(cfg.ApiKey))
        return Results.Problem("Gemini API is not configured.", statusCode: 503);
    var prompt = $"""
    You are Daybook Speaking Coach. The user is completing a SOLO speaking-practice session.
    Respond in Indonesian unless the transcript is clearly in another language.
    Evaluate only what can reasonably be inferred from the transcript. Do not infer vocal tone, pace, confidence, pronunciation, body language or identity from text.
    If the transcript appears to contain multiple speakers or dialogue, clearly state that individual attribution is uncertain and do not claim that every utterance belongs to the user.
    The goal is to help the user learn to think and speak in a structured way, not to become dependent on AI.

    Return constructive, specific feedback using exactly these six dimensions:
    1. Structure — Is there a clear opening/context, main point, supporting explanation, and conclusion?
    2. Clarity — Can a listener understand the ideas without guessing missing links?
    3. Context — Does the speaker give enough background for a listener who is unfamiliar with the topic?
    4. Reasoning — Are claims supported by reasons, examples, evidence, or logical connections?
    5. Conciseness — Is the message focused, with unnecessary repetition or detours identified?
    6. Conclusion — Does the speaker close with a clear takeaway, decision, or next step when appropriate?

    For EACH dimension:
    - Give an assessment in plain language (do not give numeric scores or pretend this is an objective measurement).
    - Quote a short, exact excerpt from the transcript as evidence. If there is no useful evidence, say so instead of inventing a quote.
    - Explain why the excerpt helps or weakens the communication.
    - Give one practical improvement.

    Then include:
    A. What the speaker successfully communicated (brief summary).
    B. The 1-3 highest-impact improvements, prioritized.
    C. A better-structured example that preserves the speaker's meaning and does not invent facts.
    D. One focused exercise for the next attempt.
    E. A retry prompt suitable for a 1-2 minute response.

    Be encouraging but candid. Do not confuse transcript quality with speaking ability. Mention that pauses, tone, pronunciation and vocal delivery cannot be assessed from this transcript-only endpoint.
    Transcript:
    {request.Transcript}
    """;
    var payload = new { contents = new[] { new { parts = new[] { new { text = prompt } } } },
        generationConfig = new { temperature = 0.3 } };
    var candidates = new[] { cfg.Model, cfg.FallbackModel }.Where(x => !string.IsNullOrWhiteSpace(x))
        .Distinct(StringComparer.OrdinalIgnoreCase);
    foreach (var model in candidates)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeoutSeconds = Math.Clamp(cfg.RequestTimeoutSeconds, 5, 90);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var client = clients.CreateClient("gemini");
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{cfg.EndpointBase.TrimEnd('/')}/{model}:generateContent");
        message.Headers.Add("x-goog-api-key", cfg.ApiKey);
        message.Content = JsonContent.Create(payload);
        try
        {
            logger.LogInformation("Speaking Coach calling Gemini model {GeminiModel} with timeout {TimeoutSeconds}s", model, timeoutSeconds);
            using var response = await client.SendAsync(message, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Speaking Coach Gemini model {GeminiModel} returned HTTP {StatusCode}: {ResponseBody}",
                    model, (int)response.StatusCode, body.Length > 2000 ? body[..2000] : body);
                continue;
            }

            using var doc = JsonDocument.Parse(body);
            var coaching = doc.RootElement.GetProperty("candidates")[0].GetProperty("content")
                .GetProperty("parts")[0].GetProperty("text").GetString();
            if (!string.IsNullOrWhiteSpace(coaching))
            {
                logger.LogInformation("Speaking Coach Gemini model {GeminiModel} succeeded", model);
                return Results.Ok(new { coaching });
            }

            logger.LogWarning("Speaking Coach Gemini model {GeminiModel} returned an empty response", model);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(ex, "Speaking Coach Gemini model {GeminiModel} timed out after {TimeoutSeconds}s",
                model, timeoutSeconds);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Speaking Coach Gemini model {GeminiModel} request failed", model);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Speaking Coach Gemini model {GeminiModel} returned invalid JSON", model);
        }
    }
    logger.LogError("Speaking Coach failed for all configured Gemini models. Primary={PrimaryModel}; Fallback={FallbackModel}",
        cfg.Model, cfg.FallbackModel);
    return Results.Problem("Speaking Coach is temporarily unavailable. Check backend logs for Gemini HTTP status or timeout details.", statusCode: 502);
});


api.MapPost("/speaking/compare", async (SpeakingCompareRequest request, IOptions<GeminiOptions> geminiOptions,
    IHttpClientFactory clients, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.EarlierTranscript) || string.IsNullOrWhiteSpace(request.LaterTranscript))
        return Results.BadRequest(new { error = "Both attempt transcripts are required." });
    if (request.EarlierTranscript.Length > 50000 || request.LaterTranscript.Length > 50000)
        return Results.BadRequest(new { error = "Each transcript must be at most 50,000 characters." });
    var cfg = geminiOptions.Value;
    if (string.IsNullOrWhiteSpace(cfg.ApiKey))
        return Results.Problem("Gemini API is not configured.", statusCode: 503);

    var prompt = $"""
    You are Daybook Speaking Coach comparing two SOLO practice attempts by the same user.
    Respond in Indonesian unless both transcripts are clearly in another language.
    Topic: {request.Topic ?? "(not supplied)"}
    Earlier attempt:
    {request.EarlierTranscript}
    Later attempt:
    {request.LaterTranscript}

    Compare only evidence visible in the transcripts. Do not infer vocal tone, pace, confidence, pronunciation, body language, or facts not stated.
    Do not assign numeric scores. Do not assume the later attempt is better merely because it is later.
    Return concise Markdown with:
    1. **Perubahan yang terlihat** — compare Structure, Clarity, Context, Reasoning, Conciseness, and Conclusion; for each, say improved / similar / needs attention only when evidence supports it.
    2. **Evidence** — quote short exact excerpts from both attempts and explain the difference. Never invent quotes.
    3. **Kemajuan utama** — the most meaningful improvement, if any.
    4. **Hal yang masih berulang** — recurring weakness supported by both transcripts.
    5. **Fokus latihan berikutnya** — one specific exercise.
    6. **Encouragement** — brief, candid and constructive.
    If transcripts are too short or too different in topic to compare fairly, say so explicitly.
    """;
    var payload = new { contents = new[] { new { parts = new[] { new { text = prompt } } } },
        generationConfig = new { temperature = 0.25 } };
    var candidates = new[] { cfg.Model, cfg.FallbackModel }.Where(x => !string.IsNullOrWhiteSpace(x))
        .Distinct(StringComparer.OrdinalIgnoreCase);
    foreach (var model in candidates)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(cfg.RequestTimeoutSeconds, 5, 60)));
        var client = clients.CreateClient("gemini"); client.Timeout = Timeout.InfiniteTimeSpan;
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{cfg.EndpointBase.TrimEnd('/')}/{model}:generateContent");
        message.Headers.Add("x-goog-api-key", cfg.ApiKey); message.Content = JsonContent.Create(payload);
        try
        {
            using var response = await client.SendAsync(message, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode) continue;
            using var doc = JsonDocument.Parse(body);
            var text = doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
            if (!string.IsNullOrWhiteSpace(text)) return Results.Ok(new { comparison = text });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { } catch (HttpRequestException) { } catch (JsonException) { }
    }
    return Results.Problem("Speaking Coach comparison is temporarily unavailable.", statusCode: 502);
});

// Runs a real model comparison on a user-owned audio object against a manually verified reference transcript.
// This is the only reliable way in this solution to measure WER; heuristic QC is not an accuracy metric.
api.MapPost("/benchmarks/transcription", async (TranscriptionBenchmarkRequest request, HttpContext http,
    IRecordingObjectStore objectStore, IActivityProfileProvider profiles, IAudioChunker chunker,
    ISpeechToTextProvider stt, ITranscriptQualityChecker qualityChecker, DaybookDbContext db,
    IOptions<WorkerOptions> workerOptions,
    CancellationToken ct) =>
{
    var userId = http.User.FindFirst("user_id")?.Value ?? http.User.FindFirst("sub")?.Value;
    if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(request.ActivityCode) || string.IsNullOrWhiteSpace(request.AudioObjectName) ||
        string.IsNullOrWhiteSpace(request.ReferenceTranscript) || request.Models is null || request.Models.Count == 0 || request.Models.Count > 4)
        return Results.BadRequest(new { error = "ActivityCode, owned AudioObjectName, ReferenceTranscript and 1-4 Models are required." });
    var supportedModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "large-v3", "large-v3-turbo", "whisper-large-v3", "whisper-large-v3-turbo" };
    if (request.Models.Any(x => string.IsNullOrWhiteSpace(x) || !supportedModels.Contains(x.Trim())))
        return Results.BadRequest(new { error = "Only Groq Whisper Large V3 and Large V3 Turbo models are supported in this free-tier configuration." });
    if (!request.AudioObjectName.StartsWith($"users/{userId}/", StringComparison.Ordinal)) return Results.Forbid();
    if (!await profiles.ExistsAsync(request.ActivityCode, ct)) return Results.BadRequest(new { error = "Unknown ActivityCode." });

    var profile = await profiles.GetAsync(request.ActivityCode, ct);
    var directory = Path.GetFullPath(Path.Combine(workerOptions.Value.TempDirectory, "benchmarks", Guid.NewGuid().ToString("N")));
    Directory.CreateDirectory(directory);
    var audioPath = Path.Combine(directory, "benchmark-audio.bin");
    try
    {
        await objectStore.DownloadToFileAsync(request.AudioObjectName, audioPath, ct);
        var chunkPaths = await chunker.CreateChunksAsync(audioPath, Path.Combine(directory, "chunks"), profile, ct);
        var results = new List<object>();
        foreach (var model in request.Models.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var modelChunks = new List<SttTranscriptionResult>();
            foreach (var chunkPath in chunkPaths)
                modelChunks.Add(await stt.TranscribeAsync(chunkPath, profile, model,
                    request.LanguageOverride ?? profile.Language, ct));
            var hypothesis = TranscriptStitcher.Stitch(modelChunks.Select(x => x.Text));
            var wer = WordErrorRate.Calculate(request.ReferenceTranscript, hypothesis);
            var cer = CharacterErrorRate.Calculate(request.ReferenceTranscript, hypothesis);
            var qc = modelChunks.Select(x => qualityChecker.Check(x, profile)).ToArray();
            results.Add(new { model = modelChunks.FirstOrDefault()?.Model ?? model, transcript = hypothesis, wer, cer,
                chunkQuality = qc, detectedLanguages = modelChunks.Select(x => new { x.DetectedLanguage, x.LanguageProbability }) });
        }
        var entity = new TranscriptionBenchmark { Id = Guid.NewGuid(), UserId = userId,
            ActivityCode = request.ActivityCode, AudioObjectName = request.AudioObjectName,
            ReferenceTranscript = request.ReferenceTranscript, ResultsJson = JsonSerializer.Serialize(results) };
        db.TranscriptionBenchmarks.Add(entity);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { benchmarkId = entity.Id, activityCode = entity.ActivityCode, results });
    }
    finally
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { }
    }
});

api.MapPost("/recordings/{id:guid}/retry", async (Guid id, HttpContext http, IJobRepository jobs, CancellationToken ct) =>
{
    var userId = http.User.FindFirst("user_id")?.Value ?? http.User.FindFirst("sub")?.Value;
    var accepted = await jobs.RequestRetryAsync(id, userId!, ct);
    return accepted ? Results.Accepted($"/api/recordings/{id}") : Results.Conflict(new { error = "Job cannot be retried." });
});

try
{
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly.");
}
finally
{
    Log.CloseAndFlush();
}

public sealed record CreateUploadUrlRequest(string ClientRecordingId, string ContentType = "audio/wav");
public sealed record NoteAiReviewRequest(string? Title, string Content);
public sealed record NoteAiAssistRequest(string? Title, string Content, string? Action, string? Instruction = null);
public sealed record SpeakingCoachRequest(string Transcript);

public sealed record SpeakingCompareRequest(
    string EarlierTranscript,
    string LaterTranscript,
    string? Topic = null
);

public sealed record TranscriptionBenchmarkRequest(string ActivityCode, string AudioObjectName,
    string ReferenceTranscript, List<string> Models, string? LanguageOverride = null);

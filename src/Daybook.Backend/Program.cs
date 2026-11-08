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

var builder = WebApplication.CreateBuilder(args);

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

var app = builder.Build();
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

app.Run();

public sealed record CreateUploadUrlRequest(string ClientRecordingId, string ContentType = "audio/wav");

public sealed record TranscriptionBenchmarkRequest(string ActivityCode, string AudioObjectName,
    string ReferenceTranscript, List<string> Models, string? LanguageOverride = null);


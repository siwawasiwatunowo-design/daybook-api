using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Daybook.Backend.Options;
using Microsoft.Extensions.Options;

namespace Daybook.Backend.Application;

/// <summary>
/// Context-first learning flow. The submitted source is processed on demand and is not persisted.
/// The source is treated as the factual boundary: the model must distinguish source facts from inference.
/// </summary>
public static class LearningEndpoints
{
    public static RouteGroupBuilder MapLearningEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/learning/prepare", async Task<IResult> (
            PrepareLearningRequest request,
            HttpContext http,
            IOptions<GeminiOptions> options,
            IHttpClientFactory clients,
            CancellationToken ct) =>
        {
            var userId = http.User.FindFirst("user_id")?.Value ?? http.User.FindFirst("sub")?.Value;
            if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(request.Content))
                return Results.BadRequest(new { error = "Materi tidak boleh kosong." });
            if (request.Content.Length > 30000)
                return Results.BadRequest(new { error = "Materi maksimal 30.000 karakter." });

            var prompt = $$"""
            You are Daybook's Learning Companion. Help the user understand the supplied material before asking for an opinion.
            Respond in Indonesian unless the source is clearly in another language.
            Treat the source below as the factual boundary. Do not invent facts, names, statistics, examples, causes, or conclusions.
            If the source is incomplete or ambiguous, explicitly say so. Do not add outside knowledge as if it came from the source.
            Create a compact, useful learning guide. Do not test the user yet and do not write a generic summary.
            Return ONLY one valid JSON object with this exact schema:
            {
              "title": "short, accurate title",
              "overview": "plain-language explanation of what the material is mainly about",
              "centralQuestion": "one meaningful question that captures the central issue",
              "keyIdeas": ["3 to 5 source-grounded ideas, each understandable without jargon"],
              "terms": [{"term":"term or concept","meaning":"meaning supported by source; use 'Belum dijelaskan dalam materi' if absent"}],
              "comprehensionQuestions": [
                {"question":"question answerable from the source","expectedFocus":"what a sufficient answer should demonstrate"}
              ],
              "thinkingQuestions": [
                {"question":"open question that invites analysis or a position","purpose":"what thinking this invites"}
              ],
              "sourceLimitations": ["important context or evidence missing from the supplied source, if any"]
            }
            Provide 3 comprehension questions and 2 thinking questions. Avoid trivia; test the main idea, relationships, and implications.
            Title: {request.Title ?? "(tanpa judul)"}
            Source type: {request.SourceType ?? "user-provided material"}
            MATERIAL (source of truth):
            {request.Content}
            """;

            try
            {
                var json = await GenerateJsonAsync(prompt, options.Value, clients, ct);
                return Results.Ok(new { guide = json.RootElement.Clone() });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                return Results.Problem("Daybook belum dapat menyiapkan materi. Materi yang kamu kirim tidak disimpan.",
                    statusCode: 502, extensions: new Dictionary<string, object?> { ["detail"] = ex.Message });
            }
        });

        api.MapPost("/learning/reflect", async Task<IResult> (
            ReflectLearningRequest request,
            HttpContext http,
            IOptions<GeminiOptions> options,
            IHttpClientFactory clients,
            CancellationToken ct) =>
        {
            var userId = http.User.FindFirst("user_id")?.Value ?? http.User.FindFirst("sub")?.Value;
            if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(request.Material) || string.IsNullOrWhiteSpace(request.GuideJson))
                return Results.BadRequest(new { error = "Materi dan learning guide wajib disertakan." });
            if (request.Material.Length > 30000 || request.GuideJson.Length > 15000 ||
                (request.Answers?.Sum(x => x?.Length ?? 0) ?? 0) > 10000 ||
                (request.Opinion?.Length ?? 0) > 10000)
                return Results.BadRequest(new { error = "Input melebihi batas ukuran." });

            var answers = request.Answers ?? [];
            var prompt = $$"""
            You are Daybook's thinking partner and learning coach.
            Respond in Indonesian unless the material is clearly in another language.
            Assess only what the user demonstrates in their answers. Do not infer general intelligence, personality, or stable ability from one session.
            Separate comprehension from opinion/reasoning. A weak or short answer may reflect missing context, uncertainty, or an unclear question; do not over-diagnose.
            Ground feedback in the supplied material. If an answer is unsupported, say what evidence is missing rather than calling it wrong.
            Do not reward agreement with a particular viewpoint. Evaluate whether the user's reasoning is understandable and supported.
            Return ONLY one valid JSON object:
            {
              "understandingFeedback": "specific, supportive feedback on what was understood and what remains unclear; state uncertainty where needed",
              "understood": ["specific ideas the user correctly captured, if supported"],
              "gaps": ["specific gaps or misunderstandings supported by the answers; empty if none can be established"],
              "thinkingFeedback": "feedback on how the user formed an opinion, used reasons, considered implications or perspectives; distinguish insufficient evidence",
              "followUpQuestion": "one question that helps the user deepen their own thinking without giving them the answer",
              "speakingPrompt": "a 1-2 minute prompt based on this material and the user's own position; never require speaking if they do not want to"
            }
            MATERIAL:
            {request.Material}
            LEARNING GUIDE:
            {request.GuideJson}
            USER COMPREHENSION ANSWERS:
            {string.Join("\n", answers.Select((x, i) => $"{i + 1}. {x}"))}
            USER'S OWN OPINION:
            {request.Opinion ?? ""}
            """;

            try
            {
                var json = await GenerateJsonAsync(prompt, options.Value, clients, ct);
                return Results.Ok(new { reflection = json.RootElement.Clone() });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                return Results.Problem("Refleksi belum dapat dibuat. Jawabanmu belum disimpan.",
                    statusCode: 502, extensions: new Dictionary<string, object?> { ["detail"] = ex.Message });
            }
        });

        return api;
    }

    private static async Task<JsonDocument> GenerateJsonAsync(
        string prompt, GeminiOptions cfg, IHttpClientFactory clients, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.ApiKey))
            throw new InvalidOperationException("Gemini API belum dikonfigurasi.");

        var candidates = new[] { cfg.Model, cfg.FallbackModel }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        string? lastError = null;
        foreach (var model in candidates)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(cfg.RequestTimeoutSeconds, 5, 90)));
            var client = clients.CreateClient("gemini");
            client.Timeout = Timeout.InfiniteTimeSpan;
            using var message = new HttpRequestMessage(HttpMethod.Post,
                $"{cfg.EndpointBase.TrimEnd('/')}/{model}:generateContent");
            message.Headers.Add("x-goog-api-key", cfg.ApiKey);
            message.Content = JsonContent.Create(new
            {
                contents = new[] { new { parts = new[] { new { text = prompt } } } },
                generationConfig = new { temperature = 0.2, responseMimeType = "application/json" }
            });

            try
            {
                using var response = await client.SendAsync(message, timeout.Token);
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    lastError = $"Gemini HTTP {(int)response.StatusCode}: {body}";
                    continue;
                }

                using var envelope = JsonDocument.Parse(body);
                var text = envelope.RootElement.GetProperty("candidates")[0]
                    .GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    lastError = "Gemini mengembalikan respons kosong.";
                    continue;
                }

                var cleaned = text.Trim();
                if (cleaned.StartsWith("```", StringComparison.Ordinal))
                {
                    var firstLine = cleaned.IndexOf('\n');
                    if (firstLine >= 0) cleaned = cleaned[(firstLine + 1)..];
                    if (cleaned.EndsWith("```", StringComparison.Ordinal))
                        cleaned = cleaned[..^3].Trim();
                }
                return JsonDocument.Parse(cleaned);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException ex) { lastError = ex.Message; }
            catch (HttpRequestException ex) { lastError = ex.Message; }
            catch (JsonException ex) { lastError = $"Respons AI bukan JSON valid: {ex.Message}"; }
        }

        throw new InvalidOperationException(lastError ?? "Tidak ada model Gemini yang tersedia.");
    }
}

public sealed record PrepareLearningRequest(string? Title, string Content, string? SourceType = null);
public sealed record ReflectLearningRequest(
    string Material, string GuideJson, List<string>? Answers, string? Opinion);

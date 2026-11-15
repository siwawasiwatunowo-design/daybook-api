using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Daybook.Backend.Application;
using Daybook.Backend.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Daybook.Backend.Infrastructure;

public sealed class GeminiTranscriptRefiner(
    IHttpClientFactory clients,
    IOptions<GeminiOptions> options,
    ILogger<GeminiTranscriptRefiner> logger)
    : ITranscriptRefiner
{
    private static readonly HashSet<HttpStatusCode> TransientStatusCodes =
    [
        HttpStatusCode.RequestTimeout,
        HttpStatusCode.TooManyRequests,
        HttpStatusCode.InternalServerError,
        HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout
    ];

    public async Task<RefinedChunkResult> RefineChunkAsync(string rawTranscript,
        ActivityProcessingProfile profile, CancellationToken ct)
    {
        var prompt = $"""
        You are a careful Indonesian transcription editor. Activity: {profile.DisplayName}.
        Context profile: {profile.RefinementProfile}.
        Vocabulary/context: {profile.VocabularyInstructions}
        Important vocabulary: {string.Join(", ", profile.VocabularyTerms.Concat(profile.VocabularyAliases))}
        Rules:
        - Treat the raw transcript as the only source of truth. You are an editor, not a second speech recognizer.
        - Preserve every factual claim, names, acronyms, numbers, dates, product/system names and technical terms exactly.
        - Do not expand, normalize, translate, or silently correct acronyms or proper nouns.
        - Do not add words or reconstruct missing speech from general knowledge or meeting context.
        - If a phrase is unclear or contradictory, preserve it and mark [perlu verifikasi] rather than guessing.
        - You may correct punctuation, capitalization and paragraph boundaries only when meaning is unchanged.
        - Produce a concise factual summary of this audio segment for later meeting synthesis.
        - Keep uncertain terms visibly uncertain in both transcript and summary: {(profile.PreserveUncertainTerms ? "mark them [perlu verifikasi], never guess" : "do not invent missing content")}.
        - Write in the same language as the raw transcript unless the activity profile explicitly specifies otherwise.
        - Return JSON only with keys refinedTranscript and chunkSummaryJson.

        RAW TRANSCRIPT SEGMENT:
        {rawTranscript}
        """;
        var json = await GenerateJsonAsync(prompt, ct);
        var refined = json.TryGetProperty("refinedTranscript", out var r) ? r.GetString() ?? rawTranscript : rawTranscript;
        var summary = json.TryGetProperty("chunkSummaryJson", out var s)
            ? s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : s.GetRawText()
            : "";
        return new RefinedChunkResult(refined, summary);
    }

    public async Task<string> GenerateFinalMomAsync(IReadOnlyList<string> chunkSummaries,
        ActivityProcessingProfile profile, CancellationToken ct)
    {
        if (!profile.GenerateMom) return "";
        var level = chunkSummaries.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (level.Count == 0) return "{}";
        while (level.Count > 10)
        {
            var next = new List<string>();
            foreach (var batch in level.Chunk(10))
            {
                var batchPrompt = $"""
                Condense these segment summaries into a factual intermediate summary for activity '{profile.DisplayName}'.
                Preserve decisions, action items, names, numbers, owners and deadlines only when explicitly present.
                Do not infer missing facts. Return concise JSON only with one key: summary.
                SEGMENT SUMMARIES:
                {string.Join("\n\n", batch)}
                """;
                var result = await GenerateJsonAsync(batchPrompt, ct);
                next.Add(result.TryGetProperty("summary", out var summary)
                    ? summary.ValueKind == JsonValueKind.String ? summary.GetString() ?? "" : summary.GetRawText()
                    : "");
            }
            level = next;
        }

        var finalPrompt = $"""
        Create the final meeting minutes (MoM) for activity '{profile.DisplayName}' from the segment summaries below.
        Output valid JSON with keys: title, overview, discussionPoints (array), decisions (array), actionItems (array), openQuestions (array).
        Each action item may contain task, owner, deadline. Use null/empty when not stated. Never invent facts.
        If the source does not support a decision or owner, say it was not specified.
        SEGMENT SUMMARIES:
        {string.Join("\n\n", level)}
        """;
        var final = await GenerateJsonAsync(finalPrompt, ct);
        return final.GetRawText();
    }

    private async Task<JsonElement> GenerateJsonAsync(string prompt, CancellationToken ct)
    {
        var cfg = options.Value;
        if (string.IsNullOrWhiteSpace(cfg.ApiKey))
            throw new InvalidOperationException("Gemini:ApiKey is not configured.");

        var candidateModels = new[] { cfg.Model, cfg.FallbackModel, "gemini-2.5-flash-lite" }
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ? m["models/".Length..] : m)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var payload = new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            generationConfig = new { responseMimeType = "application/json", temperature = 0.1 }
        };

        var client = clients.CreateClient("gemini");
        client.Timeout = TimeSpan.FromMinutes(Math.Max(1, cfg.TimeoutMinutes));

        Exception? lastError = null;

        foreach (var model in candidateModels)
        {
            var endpoint = $"{cfg.EndpointBase.TrimEnd('/')}/{model}:generateContent";

            for (var attempt = 1; attempt <= 2; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = JsonContent.Create(payload)
                };
                request.Headers.Add("x-goog-api-key", cfg.ApiKey);

                HttpResponseMessage response;
                try
                {
                    response = await client.SendAsync(request, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (TaskCanceledException ex)
                {
                    lastError = new TimeoutException($"Gemini request timed out for model '{model}' on attempt {attempt}.", ex);
                    logger.LogWarning(ex, "Gemini request timed out (Model: {Model}, Attempt: {Attempt}).", model, attempt);
                    if (attempt < 2)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
                        continue;
                    }
                    break;
                }
                catch (HttpRequestException ex)
                {
                    lastError = ex;
                    logger.LogWarning(ex, "Gemini transport error (Model: {Model}, Attempt: {Attempt}).", model, attempt);
                    if (attempt < 2)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
                        continue;
                    }
                    break;
                }

                using var responseScope = response;
                var body = await response.Content.ReadAsStringAsync(ct);

                if (response.IsSuccessStatusCode)
                {
                    using var envelope = JsonDocument.Parse(body);
                    var text = envelope.RootElement.GetProperty("candidates")[0].GetProperty("content")
                        .GetProperty("parts")[0].GetProperty("text").GetString() ?? "{}";
                    using var parsed = JsonDocument.Parse(text);
                    return parsed.RootElement.Clone();
                }

                logger.LogWarning(
                    "Gemini request failed (Model: {Model}, Attempt: {Attempt}, Status: {StatusCode}). Response: {Body}",
                    model, attempt, (int)response.StatusCode, body);

                lastError = new HttpRequestException(
                    $"Gemini API error ({(int)response.StatusCode} {response.StatusCode}) on model '{model}': {ExtractGoogleErrorMessage(body)}",
                    null,
                    response.StatusCode);

                if (response.StatusCode == HttpStatusCode.NotFound)
                    break;

                if (TransientStatusCodes.Contains(response.StatusCode) && attempt < 2)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
                    continue;
                }

                break;
            }
        }

        throw lastError ?? new InvalidOperationException("Gemini generation failed across all candidate models.");
    }

    private static string ExtractGoogleErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.TryGetProperty("message", out var msg))
            {
                return msg.GetString() ?? body;
            }
        }
        catch { }

        return body.Length > 300 ? body[..300] : body;
    }
}

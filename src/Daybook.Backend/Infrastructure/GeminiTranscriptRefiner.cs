using System.Net.Http.Json;
using System.Text.Json;
using Daybook.Backend.Application;
using Daybook.Backend.Options;
using Microsoft.Extensions.Options;

namespace Daybook.Backend.Infrastructure;

public sealed class GeminiTranscriptRefiner(IHttpClientFactory clients, IOptions<GeminiOptions> options)
    : ITranscriptRefiner
{
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
        // Hierarchical synthesis prevents a long recording's full transcript from being sent
        // in one Gemini request. First condense batches of at most 10 segment summaries.
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
        var endpoint = $"{cfg.EndpointBase.TrimEnd('/')}/{cfg.Model}:generateContent";
        var payload = new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            generationConfig = new { responseMimeType = "application/json", temperature = 0.1 }
        };
        var client = clients.CreateClient("gemini");
        client.Timeout = TimeSpan.FromMinutes(Math.Max(1, cfg.TimeoutMinutes));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add("x-goog-api-key", cfg.ApiKey);
        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();
        using var envelope = JsonDocument.Parse(body);
        var text = envelope.RootElement.GetProperty("candidates")[0].GetProperty("content")
            .GetProperty("parts")[0].GetProperty("text").GetString() ?? "{}";
        using var parsed = JsonDocument.Parse(text);
        return parsed.RootElement.Clone();
    }
}

using System.Net.Http.Headers;
using System.Text.Json;
using Daybook.Backend.Application;
using Daybook.Backend.Options;
using Microsoft.Extensions.Options;

namespace Daybook.Backend.Infrastructure;

// Groq-hosted Whisper API. The backend still chunks audio locally before sending each chunk.
public sealed class GroqSpeechToTextProvider(IHttpClientFactory clients, IOptions<SttOptions> options)
    : ISpeechToTextProvider
{
    public async Task<SttTranscriptionResult> TranscribeAsync(string audioPath,
        ActivityProcessingProfile profile, string model, string language, CancellationToken ct)
    {
        var config = options.Value;
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            throw new InvalidOperationException("Set Stt:ApiKey using .NET user-secrets or environment variable Stt__ApiKey.");
        if (!File.Exists(audioPath)) throw new FileNotFoundException("STT chunk file not found.", audioPath);
        var size = new FileInfo(audioPath).Length;
        var maxBytes = Math.Max(1, config.MaxUploadMegabytes) * 1024L * 1024L;
        if (size > maxBytes)
            throw new InvalidOperationException($"STT chunk is {size / 1024d / 1024d:F1} MB; configured limit is {config.MaxUploadMegabytes} MB. Reduce ActivityProfiles:Profiles:ChunkDurationMinutes.");

        var client = clients.CreateClient("stt");
        client.Timeout = TimeSpan.FromMinutes(Math.Max(1, config.TimeoutMinutes));
        using var request = new HttpRequestMessage(HttpMethod.Post, config.Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        using var form = new MultipartFormDataContent();
        await using var stream = File.OpenRead(audioPath);
        using var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", Path.GetFileName(audioPath));
        form.Add(new StringContent(NormalizeModel(model)), "model");
        form.Add(new StringContent(language), "language");
        form.Add(new StringContent("verbose_json"), "response_format");
        form.Add(new StringContent("segment"), "timestamp_granularities[]");
        var prompt = BuildVocabularyPrompt(profile);
        if (!string.IsNullOrWhiteSpace(prompt)) form.Add(new StringContent(prompt), "prompt");
        form.Add(new StringContent("0"), "temperature");
        request.Content = form;

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if ((int)response.StatusCode == 429)
        {
            var retryAfter = response.Headers.RetryAfter?.ToString();
            throw new InvalidOperationException($"Groq free-tier rate limit/quota reached (HTTP 429). Retry after the quota resets. Retry-After: {retryAfter ?? "not provided"}. Completed chunks are checkpointed and will be skipped when the job is retried. Details: {body}");
        }
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Groq STT returned HTTP {(int)response.StatusCode}: {body}");

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var segments = new List<SttSegmentResult>();
        if (TryGet(root, "segments", out var segmentArray) && segmentArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in segmentArray.EnumerateArray())
                segments.Add(new SttSegmentResult(GetString(item, "text") ?? "",
                    GetDouble(item, "avg_logprob", "avgLogProb"), GetDouble(item, "no_speech_prob", "noSpeechProb"),
                    GetDouble(item, "compression_ratio", "compressionRatio"), GetDouble(item, "start"), GetDouble(item, "end")));
        }
        var transcript = GetString(root, "text") ?? "";
        if (string.IsNullOrWhiteSpace(transcript)) throw new InvalidOperationException("Groq STT returned an empty transcript.");
        return new SttTranscriptionResult(transcript, GetString(root, "model") ?? NormalizeModel(model),
            GetString(root, "language"), null, GetDouble(root, "duration"), segments);
    }

    private static string NormalizeModel(string model)
    {
        var value = model.Trim().ToLowerInvariant();
        if (value.StartsWith("whisper-")) value = value[8..];
        return value switch
        {
            "large-v3" => "whisper-large-v3",
            "large-v3-turbo" => "whisper-large-v3-turbo",
            _ => throw new ArgumentException("Groq provider supports whisper-large-v3 and whisper-large-v3-turbo only.", nameof(model))
        };
    }

    private static string BuildVocabularyPrompt(ActivityProcessingProfile profile)
    {
        var terms = profile.VocabularyTerms.Concat(profile.VocabularyAliases)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase);
        var glossary = string.Join(", ", terms);
        var prompt = string.Join(" ", new[] { profile.VocabularyInstructions,
            glossary.Length > 0 ? $"Important terms and spellings: {glossary}." : "" });
        return prompt.Length <= 700 ? prompt : prompt[..700];
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value) =>
        element.TryGetProperty(name, out value);
    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static double? GetDouble(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        return null;
    }
}

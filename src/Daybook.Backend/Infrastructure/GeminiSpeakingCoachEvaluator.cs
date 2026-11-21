using System.Text.Json;
using System.Collections.Concurrent;
using System.Net;
using Daybook.Backend.Application;
using Daybook.Backend.Options;
using Microsoft.Extensions.Options;

namespace Daybook.Backend.Infrastructure;

/// <summary>
/// Shared Gemini evaluator used by the durable Speaking Coach evaluation worker.
/// </summary>
public sealed class GeminiSpeakingCoachEvaluator(
    IOptions<GeminiOptions> geminiOptions,
    IHttpClientFactory clients,
    IConfiguration configuration,
    ILogger<GeminiSpeakingCoachEvaluator> logger) : ISpeakingCoachEvaluator
{
    private static readonly ConcurrentDictionary<string, DateTimeOffset> ModelCooldowns = new(StringComparer.OrdinalIgnoreCase);

    public async Task<string> EvaluateAsync(string transcript, string? topic, CancellationToken ct, string? userFeedback = null)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            throw new ArgumentException("Transcript is required.", nameof(transcript));

        var cfg = geminiOptions.Value;
        if (string.IsNullOrWhiteSpace(cfg.ApiKey))
            throw new InvalidOperationException("Gemini API is not configured.");

        var topicText = string.IsNullOrWhiteSpace(topic) ? "(not supplied)" : topic.Trim();
        var prompt = $"""
        You are Daybook Speaking Coach. The user is completing a SOLO speaking-practice session.
        Respond in Indonesian unless the transcript is clearly in another language.
        Practice topic: {topicText}
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
        User feedback about the previous evaluation (optional; use it as a request to re-check evidence, not as a fact):
        {(string.IsNullOrWhiteSpace(userFeedback) ? "(none)" : userFeedback.Trim())}

        Transcript:
        {transcript}
        """;

        var payload = new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            generationConfig = new { temperature = 0.3 }
        };
        var configuredFallbacks = configuration.GetSection("Gemini:FallbackModels").Get<string[]>() ?? [];
        var candidates = new[] { cfg.Model }
            .Concat(configuredFallbacks)
            .Append(cfg.FallbackModel)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string? lastFailure = null;
        foreach (var model in candidates)
        {
            if (ModelCooldowns.TryGetValue(model, out var until) && until > DateTimeOffset.UtcNow)
            {
                logger.LogInformation("Skipping Gemini model {GeminiModel}; circuit cooldown until {CooldownUntil}.", model, until);
                continue;
            }

            // Retry only transient failures. Invalid requests/auth failures move to the next
            // configured model without wasting time repeating the same request.
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var timeoutSeconds = Math.Clamp(cfg.RequestTimeoutSeconds, 5, 90);
                timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                var client = clients.CreateClient("gemini");
                client.Timeout = Timeout.InfiniteTimeSpan;
                using var message = new HttpRequestMessage(
                    HttpMethod.Post, $"{cfg.EndpointBase.TrimEnd('/')}/{model}:generateContent");
                message.Headers.Add("x-goog-api-key", cfg.ApiKey);
                message.Content = JsonContent.Create(payload);

                try
                {
                    logger.LogInformation(
                        "Speaking Coach calling Gemini model {GeminiModel}, attempt {Attempt}, timeout {TimeoutSeconds}s",
                        model, attempt, timeoutSeconds);
                    using var response = await client.SendAsync(message, timeout.Token);
                    var body = await response.Content.ReadAsStringAsync(timeout.Token);
                    if (!response.IsSuccessStatusCode)
                    {
                        lastFailure = $"Gemini model {model} returned HTTP {(int)response.StatusCode}: {body}";
                        var code = (int)response.StatusCode;
                        var transient = response.StatusCode == HttpStatusCode.TooManyRequests ||
                            code is 500 or 502 or 503 or 504;
                        logger.LogWarning("Speaking Coach {Failure}", lastFailure.Length > 2000 ? lastFailure[..2000] : lastFailure);
                        if (code == 404) ModelCooldowns[model] = DateTimeOffset.UtcNow.AddMinutes(30);
                        else if (response.StatusCode == HttpStatusCode.TooManyRequests) ModelCooldowns[model] = DateTimeOffset.UtcNow.AddSeconds(30);
                        else if (code is 500 or 502 or 503 or 504) ModelCooldowns[model] = DateTimeOffset.UtcNow.AddSeconds(45);
                        if (transient && attempt < 2)
                        {
                            var delay = response.Headers.RetryAfter?.Delta;
                            await Task.Delay(delay is { } d ? TimeSpan.FromMilliseconds(Math.Clamp(d.TotalMilliseconds, 250, 3000))
                                : TimeSpan.FromMilliseconds(500 * attempt), ct);
                            continue;
                        }
                        break;
                    }

                    using var document = JsonDocument.Parse(body);
                    if (document.RootElement.TryGetProperty("candidates", out var candidatesElement) &&
                        candidatesElement.ValueKind == JsonValueKind.Array && candidatesElement.GetArrayLength() > 0)
                    {
                        var candidate = candidatesElement[0];
                        if (candidate.TryGetProperty("content", out var content) &&
                            content.TryGetProperty("parts", out var parts) &&
                            parts.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var part in parts.EnumerateArray())
                            {
                                if (part.TryGetProperty("text", out var textElement))
                                {
                                    var text = textElement.GetString();
                                    if (!string.IsNullOrWhiteSpace(text))
                                    {
                                        ModelCooldowns.TryRemove(model, out _);
                                        logger.LogInformation("Speaking Coach Gemini model {GeminiModel} succeeded", model);
                                        return text;
                                    }
                                }
                            }
                        }
                    }
                    lastFailure = $"Gemini model {model} returned no usable text candidate.";
                    logger.LogWarning("Speaking Coach {Failure}", lastFailure);
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (OperationCanceledException ex)
                {
                    lastFailure = $"Gemini model {model} timed out after {timeoutSeconds}s: {ex.Message}";
                    ModelCooldowns[model] = DateTimeOffset.UtcNow.AddMinutes(2);
                    logger.LogWarning(ex, "Speaking Coach Gemini model {GeminiModel} timed out", model);
                    break;
                }
                catch (HttpRequestException ex)
                {
                    lastFailure = $"Gemini model {model} request failed: {ex.Message}";
                    ModelCooldowns[model] = DateTimeOffset.UtcNow.AddMinutes(1);
                    logger.LogWarning(ex, "Speaking Coach Gemini model {GeminiModel} request failed", model);
                    break;
                }
                catch (JsonException ex)
                {
                    lastFailure = $"Gemini model {model} returned invalid JSON: {ex.Message}";
                    logger.LogError(ex, "Speaking Coach Gemini model {GeminiModel} returned invalid JSON", model);
                    break;
                }
                catch (InvalidOperationException ex)
                {
                    lastFailure = $"Gemini model {model} returned an unexpected response: {ex.Message}";
                    logger.LogError(ex, "Speaking Coach Gemini model {GeminiModel} returned an unexpected response", model);
                    break;
                }
            }
        }

        logger.LogError(
            "Speaking Coach failed for all configured Gemini models. Primary={PrimaryModel}; Fallback={FallbackModel}",
            cfg.Model, cfg.FallbackModel);
        throw new InvalidOperationException(lastFailure ?? "No Gemini model is configured for Speaking Coach.");
    }
}

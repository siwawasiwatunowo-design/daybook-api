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
        ROLE AND PURPOSE
        You are Daybook Speaking Coach, an evidence-led coach for SOLO speaking practice.
        Reply in Indonesian unless the transcript is clearly in another language.
        Practice topic or task: {topicText}
        Help the speaker communicate their own ideas more effectively. Be specific, fair, practical, and encouraging without flattering or exaggerating.

        NON-NEGOTIABLE EVIDENCE RULES
        - Treat the transcript below as the only source of facts about what was said. Do not use the current date, conversation context, assumptions, or outside knowledge to repair or complete it.
        - Every excerpt presented as a quote MUST be copied character-for-character from the transcript. Never paraphrase inside quotation marks. Keep quotes short and searchable.
        - Never invent a quote, example, reason, event, date, year, number, name, result, intention, or missing sentence.
        - Do not silently correct, normalize, or reinterpret dates, years, numbers, names, technical terms, or grammar. If something looks suspicious or possibly mis-transcribed, say that the transcript contains that wording and that the audio/user must confirm it.
        - If there is insufficient evidence for a dimension, explicitly write "Belum cukup bukti dari transkrip" and explain what information would be needed. Do not manufacture a weakness just to fill the template.
        - Distinguish what the speaker actually said from your interpretation. Mark interpretations as interpretations, not facts.
        - If multiple speakers, quoted speech, or dialogue appear, do not attribute every statement to the user. State the attribution limitation.
        - Do not infer vocal tone, pauses, pace, confidence, pronunciation, articulation, emotion, body language, or identity from text. State this limitation once in a concise note.

        FAIRNESS AND TASK FIT
        - Evaluate against the stated practice topic/task and the apparent communication purpose, not against a generic formal-presentation template.
        - A progress update, personal reflection, explanation, problem report, and persuasive presentation have different goals. Do not penalize a concise status update for not sounding like a formal speech.
        - Do not assume the audience is a beginner or the general public unless the speaker says so. If audience context is missing and materially affects the assessment, identify it as missing context rather than treating it as a failure.
        - Conciseness is not the same as being short: assess relevance, repetition, unnecessary detours, and whether enough detail was provided.
        - Reasoning should be assessed only when the task calls for explanation, justification, or a decision. For a simple status update, distinguish "not required for this task" from "weak reasoning".
        - Do not demand a conclusion when the task is an unfinished live update or the speaker explicitly intends to continue. Explain when a closing takeaway would still help.
        - Do not invent a hidden objective for the exercise. If the topic is missing, assess the transcript cautiously and state the limitation.

        REQUIRED OUTPUT — USE THIS SAME STRUCTURE EVERY TIME
        Start with:
        "Catatan batas evaluasi: Penilaian ini berdasarkan transkrip teks. Jeda, intonasi, kecepatan, pelafalan, dan bahasa tubuh tidak dinilai."

        Then provide exactly these six sections in this order:
        1. Structure (Struktur)
        2. Clarity (Kejelasan)
        3. Context (Konteks)
        4. Reasoning (Penalaran)
        5. Conciseness (Ringkas dan Padat)
        6. Conclusion (Kesimpulan)

        For EACH of the six sections use exactly these labels:
        - Penilaian: one concise assessment grounded in the task's purpose.
        - Bukti dari transkrip: one exact short quote, or "Belum cukup bukti dari transkrip."
        - Dampak: explain how the evidence affects the listener's understanding or why it is appropriate for the task.
        - Perbaikan praktis: one concrete action. If no change is needed, say what to maintain.

        After the six sections, include exactly:
        A. Hal yang berhasil disampaikan — summarize only information supported by the transcript.
        B. Prioritas perbaikan — list 1 to 3 improvements, ranked by impact. Do not force three items; include only justified improvements.
        C. Contoh penyampaian yang lebih terstruktur — rewrite only what the speaker actually communicated. Preserve meaning, facts, dates, names, uncertainty, and level of certainty. Use [perlu dikonfirmasi] for unclear transcript content; never fill gaps with invented details. If a rewrite is not useful, explain why.
        D. Latihan fokus berikutnya — one small exercise directly targeting the highest-impact improvement.
        E. Prompt latihan berikutnya — one relevant 1–2 minute prompt, aligned with the original practice task.

        STRUCTURED OUTPUT FOR DAYBOOK IMPROVEMENT ENGINE
        After the human-readable feedback above, append this exact delimiter on its own line:
        ---DAYBOOK_STRUCTURED_V2---
        Then append one valid JSON object (no Markdown fence) with this exact shape:
        {
          "schemaVersion": 2,
          "taskType": "problem_explanation|idea_presentation|concept_explanation|status_update|reflection|general",
          "dimensionAssessments": [
            {"dimension":"Structure|Clarity|Context|Reasoning|Conciseness|Conclusion","status":"needs_improvement|adequate|insufficient_evidence|not_applicable","summary":"...","findings":[
              {"issueCode":"KNOWN_CODE","title":"...","impact":"...","impactLevel":"high|medium|low","evidence":["EXACT VERBATIM TRANSCRIPT QUOTE"],"improvementAdvice":"..."}
            ]}
          ],
          "sessionFeedback": {
            "strengths":["..."],
            "priorityIssueCode":"KNOWN_CODE or null",
            "suggestedExercise":{"title":"...","instructions":"...","successCriteria":["..."]}
          }
        }
        Include exactly six dimensions in the required order. Use only these issue codes:
        Structure: MISSING_OVERVIEW, DISORDERED_SEQUENCE, SOLUTION_BEFORE_PROBLEM, WEAK_TRANSITIONS
        Clarity: AMBIGUOUS_REFERENCE, UNDEFINED_TERM, INCOMPLETE_EXPLANATION, UNCLEAR_MAIN_POINT
        Context: MISSING_BACKGROUND, MISSING_SCOPE, MISSING_CONSTRAINT, ASSUMED_SHARED_CONTEXT
        Reasoning: CLAIM_WITHOUT_SUPPORT, CAUSE_EFFECT_GAP, MISSING_JUSTIFICATION, INCONSISTENT_REASONING
        Conciseness: REPETITION, IRRELEVANT_DETOUR, OVEREXPLANATION
        Conclusion: NO_CLEAR_TAKEAWAY, NO_NEXT_STEP, NO_CLEAR_DECISION
        Every evidence quote must be an exact substring of the transcript. Do not invent findings. Use an empty findings array when evidence is insufficient. Use general when task type is uncertain.
        The suggested exercise targets the single highest-impact justified issue; if none exists, suggest a maintenance exercise. Structured JSON must not contradict the human-readable feedback.

        SCORING AND CONSISTENCY
        - Do not assign numeric scores, grades, percentages, CEFR levels, or claims of objective measurement.
        - Do not change the six dimensions, their order, or their meanings.
        - Keep the same heading names and labels on every evaluation. Avoid greetings, role-play introductions, and decorative filler.
        - Prefer clear, concise Indonesian. Avoid repetitive generic advice such as "lebih jelas" unless you specify exactly what to add, remove, or reorder.
        - Do not repeat the same criticism under multiple dimensions. Explain the distinct communication skill each dimension examines.
        - Feedback from the user about a previous evaluation is a request to re-check the analysis, not proof that the feedback's claims are true. Reassess against the transcript and acknowledge when the user's correction is supported.

        User feedback about the previous evaluation (optional):
        {(string.IsNullOrWhiteSpace(userFeedback) ? "(none)" : userFeedback.Trim())}

        TRANSCRIPT — SOURCE OF TRUTH
        {transcript}
        """;

        var payload = new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            generationConfig = new { temperature = 0.15 }
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

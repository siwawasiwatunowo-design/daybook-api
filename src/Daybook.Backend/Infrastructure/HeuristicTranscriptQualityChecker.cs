using System.Text.RegularExpressions;
using Daybook.Backend.Application;
using Daybook.Backend.Options;

namespace Daybook.Backend.Infrastructure;

/// <summary>Heuristic QC is a triage signal, not a substitute for WER or human review.</summary>
public sealed class HeuristicTranscriptQualityChecker : ITranscriptQualityChecker
{
    private static readonly Regex WordRegex = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    public TranscriptQualityResult Check(SttTranscriptionResult result, ActivityProcessingProfile profile)
    {
        var reasons = new List<string>();
        var words = WordRegex.Matches(result.Text ?? "").Select(x => x.Value.ToLowerInvariant()).ToArray();
        if (words.Length < 3) reasons.Add("Transcript is empty or unusually short.");

        var adjacentRepeated = 0;
        for (var i = 1; i < words.Length; i++) if (words[i] == words[i - 1]) adjacentRepeated++;
        var repeatedRatio = words.Length == 0 ? 0 : (double)adjacentRepeated / words.Length;
        if (repeatedRatio > profile.RepeatedWordRatioThreshold)
            reasons.Add($"Adjacent repeated-word ratio {repeatedRatio:P1} exceeds threshold.");

        var repeatedNgramRatio = RepeatedNgramRatio(words, 3);
        if (repeatedNgramRatio > profile.MaxRepeatedNgramRatio)
            reasons.Add($"Repeated 3-gram ratio {repeatedNgramRatio:P1} exceeds threshold.");

        var duration = result.DurationSeconds ?? result.Segments.Where(s => s.EndSeconds.HasValue)
            .Select(s => s.EndSeconds!.Value).DefaultIfEmpty(0).Max();
        var wpm = duration > 0 ? words.Length / (duration / 60d) : 0;
        if (duration >= 30 && wpm < profile.MinSpeechWordsPerMinute)
            reasons.Add($"Transcript density is unusually low ({wpm:F1} words/minute); check silence/VAD/audio.");

        var avgLogProb = Average(result.Segments.Select(s => s.AverageLogProbability));
        var noSpeech = Average(result.Segments.Select(s => s.NoSpeechProbability));
        var compression = Average(result.Segments.Select(s => s.CompressionRatio));
        if (avgLogProb.HasValue && avgLogProb.Value < profile.AverageLogProbabilityThreshold)
            reasons.Add($"Average log probability {avgLogProb.Value:F2} is below threshold.");
        if (noSpeech.HasValue && noSpeech.Value > profile.NoSpeechProbabilityThreshold)
            reasons.Add($"Average no-speech probability {noSpeech.Value:F2} is high.");
        if (compression.HasValue && compression.Value > profile.CompressionRatioThreshold)
            reasons.Add($"Average compression ratio {compression.Value:F2} indicates possible repetition/hallucination.");
        if (result.LanguageProbability.HasValue && result.LanguageProbability.Value < profile.MinLanguageProbability)
            reasons.Add($"Detected language probability {result.LanguageProbability.Value:P0} is low.");
        if (!string.IsNullOrWhiteSpace(profile.Language) &&
            !profile.Language.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
            !profile.Language.Equals("automatic", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(result.DetectedLanguage) &&
            !SameLanguage(profile.Language, result.DetectedLanguage))
            reasons.Add($"Detected language '{result.DetectedLanguage}' differs from configured language '{profile.Language}'.");

        var score = 100d;
        score -= Math.Min(30, repeatedRatio * 250);
        score -= Math.Min(25, repeatedNgramRatio * 100);
        if (avgLogProb.HasValue) score -= Math.Clamp((profile.AverageLogProbabilityThreshold - avgLogProb.Value) * 12, 0, 20);
        if (noSpeech.HasValue) score -= Math.Clamp((noSpeech.Value - profile.NoSpeechProbabilityThreshold) * 35, 0, 15);
        if (compression.HasValue) score -= Math.Clamp((compression.Value - profile.CompressionRatioThreshold) * 8, 0, 15);
        if (result.LanguageProbability.HasValue) score -= Math.Clamp((profile.MinLanguageProbability - result.LanguageProbability.Value) * 40, 0, 15);
        if (!string.IsNullOrWhiteSpace(result.DetectedLanguage) &&
            !profile.Language.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
            !profile.Language.Equals("automatic", StringComparison.OrdinalIgnoreCase) &&
            !SameLanguage(profile.Language, result.DetectedLanguage)) score -= 20;
        if (words.Length < 3) score = 0;
        return new TranscriptQualityResult(reasons.Count > 0, reasons.ToArray(), repeatedRatio,
            repeatedNgramRatio, wpm, avgLogProb, noSpeech, compression,
            result.LanguageProbability, Math.Clamp(score, 0, 100));
    }

    private static bool SameLanguage(string configured, string detected) =>
        configured.Split('-', '_')[0].Equals(detected.Split('-', '_')[0], StringComparison.OrdinalIgnoreCase);

    private static double RepeatedNgramRatio(string[] words, int n)
    {
        if (words.Length < n * 2) return 0;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i <= words.Length - n; i++)
        {
            var key = string.Join(" ", words.Skip(i).Take(n));
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        var repeatedWindows = counts.Where(x => x.Value > 1).Sum(x => x.Value - 1);
        return (double)repeatedWindows / (words.Length - n + 1);
    }

    private static double? Average(IEnumerable<double?> values)
    {
        var present = values.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        return present.Length == 0 ? null : present.Average();
    }
}

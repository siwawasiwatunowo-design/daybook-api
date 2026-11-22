using System.Text.Json;

namespace Daybook.Backend.Application;

public sealed record ParsedSpeakingCoachResult(string Feedback, string? StructuredJson, string ValidationStatus);

public static class SpeakingCoachStructuredResult
{
    public const string Marker = "---DAYBOOK_STRUCTURED_V2---";
    private static readonly string[] Dimensions = ["Structure", "Clarity", "Context", "Reasoning", "Conciseness", "Conclusion"];
    private static readonly Dictionary<string, string[]> CodesByDimension = new(StringComparer.Ordinal)
    {
        ["Structure"] = ["MISSING_OVERVIEW", "DISORDERED_SEQUENCE", "SOLUTION_BEFORE_PROBLEM", "WEAK_TRANSITIONS"],
        ["Clarity"] = ["AMBIGUOUS_REFERENCE", "UNDEFINED_TERM", "INCOMPLETE_EXPLANATION", "UNCLEAR_MAIN_POINT"],
        ["Context"] = ["MISSING_BACKGROUND", "MISSING_SCOPE", "MISSING_CONSTRAINT", "ASSUMED_SHARED_CONTEXT"],
        ["Reasoning"] = ["CLAIM_WITHOUT_SUPPORT", "CAUSE_EFFECT_GAP", "MISSING_JUSTIFICATION", "INCONSISTENT_REASONING"],
        ["Conciseness"] = ["REPETITION", "IRRELEVANT_DETOUR", "OVEREXPLANATION"],
        ["Conclusion"] = ["NO_CLEAR_TAKEAWAY", "NO_NEXT_STEP", "NO_CLEAR_DECISION"]
    };
    private static readonly HashSet<string> IssueCodes = new(StringComparer.Ordinal)
    {
        "MISSING_OVERVIEW", "DISORDERED_SEQUENCE", "SOLUTION_BEFORE_PROBLEM", "WEAK_TRANSITIONS",
        "AMBIGUOUS_REFERENCE", "UNDEFINED_TERM", "INCOMPLETE_EXPLANATION", "UNCLEAR_MAIN_POINT",
        "MISSING_BACKGROUND", "MISSING_SCOPE", "MISSING_CONSTRAINT", "ASSUMED_SHARED_CONTEXT",
        "CLAIM_WITHOUT_SUPPORT", "CAUSE_EFFECT_GAP", "MISSING_JUSTIFICATION", "INCONSISTENT_REASONING",
        "REPETITION", "IRRELEVANT_DETOUR", "OVEREXPLANATION",
        "NO_CLEAR_TAKEAWAY", "NO_NEXT_STEP", "NO_CLEAR_DECISION"
    };

    public static ParsedSpeakingCoachResult Extract(string raw, string transcript)
    {
        var markerIndex = raw.IndexOf(Marker, StringComparison.Ordinal);
        if (markerIndex < 0) return new(raw.Trim(), null, "Legacy");
        var feedback = raw[..markerIndex].Trim();
        var json = raw[(markerIndex + Marker.Length)..].Trim();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 2) throw new JsonException("Unsupported schema version.");
            var taskType = root.GetProperty("taskType").GetString();
            if (taskType is not ("problem_explanation" or "idea_presentation" or "concept_explanation" or "status_update" or "reflection" or "general"))
                throw new JsonException("Unknown task type.");
            var dimensions = root.GetProperty("dimensionAssessments");
            if (dimensions.ValueKind != JsonValueKind.Array || dimensions.GetArrayLength() != 6)
                throw new JsonException("Exactly six dimensions are required.");
            var observedCodes = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < Dimensions.Length; i++)
            {
                var item = dimensions[i];
                if (item.GetProperty("dimension").GetString() != Dimensions[i]) throw new JsonException("Dimension order is invalid.");
                var status = item.GetProperty("status").GetString();
                if (status is not ("needs_improvement" or "adequate" or "insufficient_evidence" or "not_applicable"))
                    throw new JsonException("Unknown dimension status.");
                var findings = item.GetProperty("findings");
                if (findings.ValueKind != JsonValueKind.Array) throw new JsonException("Findings must be an array.");
                if (status == "needs_improvement" && findings.GetArrayLength() == 0)
                    throw new JsonException("A needs_improvement dimension requires a finding.");
                if (status != "needs_improvement" && findings.GetArrayLength() > 0)
                    throw new JsonException("Findings require needs_improvement status.");
                foreach (var finding in findings.EnumerateArray())
                {
                    var code = finding.GetProperty("issueCode").GetString();
                    if (code is null || !IssueCodes.Contains(code) ||
                        !CodesByDimension[Dimensions[i]].Contains(code, StringComparer.Ordinal))
                        throw new JsonException("Issue code does not belong to its dimension.");
                    observedCodes.Add(code);
                    if (string.IsNullOrWhiteSpace(finding.GetProperty("title").GetString()) ||
                        string.IsNullOrWhiteSpace(finding.GetProperty("impact").GetString()) ||
                        string.IsNullOrWhiteSpace(finding.GetProperty("improvementAdvice").GetString()))
                        throw new JsonException("Finding explanation is incomplete.");
                    var level = finding.GetProperty("impactLevel").GetString();
                    if (level is not ("high" or "medium" or "low")) throw new JsonException("Unknown impact level.");
                    var evidence = finding.GetProperty("evidence");
                    if (evidence.ValueKind != JsonValueKind.Array || evidence.GetArrayLength() == 0)
                        throw new JsonException("A finding requires transcript evidence.");
                    foreach (var quote in evidence.EnumerateArray())
                    {
                        var value = quote.GetString();
                        if (string.IsNullOrWhiteSpace(value) || !transcript.Contains(value, StringComparison.Ordinal))
                            throw new JsonException("Evidence quote is not an exact transcript substring.");
                    }
                }
            }
            var session = root.GetProperty("sessionFeedback");
            var exercise = session.GetProperty("suggestedExercise");
            if (string.IsNullOrWhiteSpace(exercise.GetProperty("title").GetString()) ||
                string.IsNullOrWhiteSpace(exercise.GetProperty("instructions").GetString()) ||
                exercise.GetProperty("successCriteria").ValueKind != JsonValueKind.Array)
                throw new JsonException("Suggested exercise is incomplete.");
            var priority = session.GetProperty("priorityIssueCode");
            if (priority.ValueKind == JsonValueKind.String &&
                (!IssueCodes.Contains(priority.GetString()!) || !observedCodes.Contains(priority.GetString()!)))
                throw new JsonException("Priority issue code must reference an observed finding.");
            if (priority.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new JsonException("Priority issue code must be a known code or null.");
            if (exercise.GetProperty("successCriteria").GetArrayLength() == 0)
                throw new JsonException("Suggested exercise requires at least one success criterion.");
            return new(feedback, JsonSerializer.Serialize(root), "Valid");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return new(feedback, null, "Invalid");
        }
    }
}

using System.Text.Json;
using Daybook.Backend.Application;
using Xunit;

namespace Daybook.Backend.Tests;

public sealed class SpeakingCoachStructuredResultTests
{
    private static string BuildJson(string quote)
    {
        var dimensions = new List<object>();
        var names = new[] { "Structure", "Clarity", "Context", "Reasoning", "Conciseness", "Conclusion" };
        for (var index = 0; index < names.Length; index++)
        {
            object findings = index == 0
                ? new[] { new { issueCode = "MISSING_OVERVIEW", title = "Opening", impact = "Listener lacks an overview",
                    impactLevel = "high", evidence = new[] { quote }, improvementAdvice = "Start with context." } }
                : Array.Empty<object>();
            dimensions.Add(new { dimension = names[index], status = index == 0 ? "needs_improvement" : "adequate",
                summary = "Assessment", findings });
        }

        return JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            taskType = "general",
            dimensionAssessments = dimensions,
            sessionFeedback = new
            {
                strengths = new[] { "Clear intent" },
                priorityIssueCode = "MISSING_OVERVIEW",
                suggestedExercise = new
                {
                    title = "Practice an opening",
                    instructions = "State context first.",
                    successCriteria = new[] { "Give a one-sentence overview." }
                }
            }
        });
    }

    [Fact]
    public void Extract_ValidStructuredResult_PreservesFeedbackAndStructuredJson()
    {
        const string transcript = "Saya akan jelaskan masalahnya.";
        var raw = "Feedback yang tetap dibaca pengguna.\n" + SpeakingCoachStructuredResult.Marker + "\n" +
            BuildJson("Saya akan jelaskan masalahnya.");

        var result = SpeakingCoachStructuredResult.Extract(raw, transcript);

        Assert.Equal("Valid", result.ValidationStatus);
        Assert.Equal("Feedback yang tetap dibaca pengguna.", result.Feedback);
        Assert.NotNull(result.StructuredJson);
    }

    [Fact]
    public void Extract_NonVerbatimEvidence_RejectsStructuredData()
    {
        const string transcript = "Saya akan jelaskan masalahnya.";
        var raw = "Feedback tetap tersedia.\n" + SpeakingCoachStructuredResult.Marker + "\n" +
            BuildJson("Saya akan menjelaskan masalahnya.");

        var result = SpeakingCoachStructuredResult.Extract(raw, transcript);

        Assert.Equal("Invalid", result.ValidationStatus);
        Assert.Null(result.StructuredJson);
        Assert.Equal("Feedback tetap tersedia.", result.Feedback);
    }

    [Fact]
    public void Extract_LegacyResult_RemainsReadable()
    {
        var result = SpeakingCoachStructuredResult.Extract("Legacy feedback", "transcript");

        Assert.Equal("Legacy", result.ValidationStatus);
        Assert.Null(result.StructuredJson);
        Assert.Equal("Legacy feedback", result.Feedback);
    }
}

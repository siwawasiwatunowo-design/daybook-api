using System.Text.Json;
using Daybook.Backend.Domain;

namespace Daybook.Backend.Application;

public static class PracticeImprovementEngine
{
    private sealed record Attempt(RecordingJob Job, SpeakingCoachEvaluation Evaluation, JsonDocument Document, string TaskType);
    private sealed record Finding(Attempt Attempt, string Dimension, string Code, string Title, string Impact,
        string Level, string Quote, string Advice);

    public static object Build(IReadOnlyList<RecordingJob> jobs, IReadOnlyList<SpeakingCoachEvaluation> evaluations)
    {
        var jobMap = jobs.ToDictionary(x => x.Id);
        var attempts = evaluations
            .Where(x => x.Status == "Completed" && x.StructuredValidationStatus == "Valid" &&
                !string.IsNullOrWhiteSpace(x.StructuredResultJson) && jobMap.ContainsKey(x.RecordingJobId))
            .GroupBy(x => x.RecordingJobId)
            .Select(g => g.OrderByDescending(x => x.Version).First())
            .Select(e =>
            {
                var doc = JsonDocument.Parse(e.StructuredResultJson!);
                var task = doc.RootElement.GetProperty("taskType").GetString() ?? "general";
                return new Attempt(jobMap[e.RecordingJobId], e, doc, task);
            }).OrderBy(x => x.Job.CreatedAt).ToList();

        if (attempts.Count == 0)
            return new { hasEvidence = false, message = "Evaluasi terstruktur belum tersedia. Evaluasi lama tetap dapat dibaca, tetapi belum digunakan untuk menyimpulkan pola.", currentFocus = (object?)null, nextPractice = (object?)null, patterns = Array.Empty<object>() };

        var findings = new List<Finding>();
        foreach (var attempt in attempts)
        foreach (var dimension in attempt.Document.RootElement.GetProperty("dimensionAssessments").EnumerateArray())
        foreach (var finding in dimension.GetProperty("findings").EnumerateArray())
        {
            var evidence = finding.GetProperty("evidence");
            findings.Add(new Finding(attempt, dimension.GetProperty("dimension").GetString() ?? "",
                finding.GetProperty("issueCode").GetString() ?? "", finding.GetProperty("title").GetString() ?? "",
                finding.GetProperty("impact").GetString() ?? "", finding.GetProperty("impactLevel").GetString() ?? "low",
                evidence.GetArrayLength() > 0 ? evidence[0].GetString() ?? "" : "",
                finding.GetProperty("improvementAdvice").GetString() ?? ""));
        }

        if (findings.Count == 0)
            return new { hasEvidence = true, message = "Belum ada pola kelemahan yang didukung bukti. Pertahankan praktik yang sudah berjalan dan lanjutkan latihan.", currentFocus = (object?)null, nextPractice = (object?)null, patterns = Array.Empty<object>() };

        var selected = findings.GroupBy(x => new { x.Attempt.TaskType, x.Code, x.Dimension })
            .Select(g => new { Key = g.Key, Items = g.OrderByDescending(x => x.Attempt.Job.CreatedAt).ToList(),
                Count = g.Select(x => x.Attempt.Job.Id).Distinct().Count(),
                Severity = g.Max(x => x.Level == "high" ? 3 : x.Level == "medium" ? 2 : 1),
                Latest = g.Max(x => x.Attempt.Job.CreatedAt) })
            .OrderByDescending(x => x.Count).ThenByDescending(x => x.Severity).ThenByDescending(x => x.Latest)
            .First();

        var comparable = attempts.Where(x => x.TaskType == selected.Key.TaskType).ToList();
        var latestIssueAt = selected.Items.Max(x => x.Attempt.Job.CreatedAt);
        var positiveEvidence = comparable.Count(x => x.Job.CreatedAt > latestIssueAt &&
            x.Document.RootElement.GetProperty("dimensionAssessments").EnumerateArray().Any(d =>
                d.GetProperty("dimension").GetString() == selected.Key.Dimension &&
                d.GetProperty("status").GetString() == "adequate" &&
                !d.GetProperty("findings").EnumerateArray().Any(f => f.GetProperty("issueCode").GetString() == selected.Key.Code)));
        var status = positiveEvidence > 0 ? "Early Positive Signal" :
            selected.Count > 1 ? "Recurring Pattern" : "Initial Observation";
        var latestFinding = selected.Items[0];
        var session = latestFinding.Attempt.Document.RootElement.GetProperty("sessionFeedback");
        var exercise = session.GetProperty("suggestedExercise");
        var nextPractice = new
        {
            title = exercise.GetProperty("title").GetString() ?? "Latihan fokus berikutnya",
            instructions = exercise.GetProperty("instructions").GetString() ?? "",
            successCriteria = exercise.GetProperty("successCriteria").EnumerateArray().Select(x => x.GetString() ?? "").ToArray(),
            taskType = selected.Key.TaskType
        };
        var currentFocus = new
        {
            issueCode = selected.Key.Code, dimension = selected.Key.Dimension, title = latestFinding.Title,
            explanation = latestFinding.Impact, evidence = latestFinding.Quote, improvementAdvice = latestFinding.Advice,
            occurrenceCount = selected.Count, comparableAttempts = comparable.Count, positiveEvidenceAttempts = positiveEvidence,
            status, taskType = selected.Key.TaskType
        };
        var patterns = findings.GroupBy(x => new { x.Attempt.TaskType, x.Code, x.Dimension })
            .Select(g => new { issueCode = g.Key.Code, dimension = g.Key.Dimension, taskType = g.Key.TaskType,
                occurrences = g.Select(x => x.Attempt.Job.Id).Distinct().Count(),
                title = g.OrderByDescending(x => x.Attempt.Job.CreatedAt).First().Title })
            .OrderByDescending(x => x.occurrences).Take(5).Cast<object>().ToArray();
        return new { hasEvidence = true, message = positiveEvidence > 0
            ? "Ada sinyal positif pada latihan sejenis setelah masalah terakhir teramati. Ini belum membuktikan peningkatan yang konsisten."
            : "Fokus dipilih dari pola yang paling berdampak dan berulang pada latihan sejenis. Perkembangan akan diverifikasi dari percobaan berikutnya.",
            currentFocus, nextPractice, patterns };
    }
}

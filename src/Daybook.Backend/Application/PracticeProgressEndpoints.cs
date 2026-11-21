using Daybook.Backend.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Daybook.Backend.Application;

public static class PracticeProgressEndpoints
{
    public static RouteGroupBuilder MapPracticeProgressEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/practice/progress", async (HttpContext http, DaybookDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.FindFirst("user_id")?.Value ?? http.User.FindFirst("sub")?.Value;
            if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

            var jobs = await db.RecordingJobs.AsNoTracking()
                .Where(x => x.UserId == userId && x.EnableSpeakingCoach)
                .OrderByDescending(x => x.CreatedAt)
                .ToListAsync(ct);

            var jobIds = jobs.Select(x => x.Id).ToArray();
            var evaluations = await db.SpeakingCoachEvaluations.AsNoTracking()
                .Where(x => x.UserId == userId && jobIds.Contains(x.RecordingJobId))
                .OrderByDescending(x => x.CreatedAt)
                .ToListAsync(ct);

            static string SessionKey(string value, Guid id) =>
                string.IsNullOrWhiteSpace(value) ? id.ToString() : value;

            var totalSessions = jobs
                .Select(x => SessionKey(x.PracticeSessionId, x.Id))
                .Distinct(StringComparer.Ordinal)
                .Count();
            var evaluatedAttempts = evaluations
                .Where(x => string.Equals(x.Status, "Completed", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.RecordingJobId)
                .Distinct()
                .Count();

            var now = DateTimeOffset.UtcNow;
            var utcMidnight = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
            var currentWeekStart = utcMidnight.AddDays(-(((int)utcMidnight.DayOfWeek + 6) % 7));
            var weeks = Enumerable.Range(0, 8).Select(offset =>
            {
                var start = currentWeekStart.AddDays(-7 * (7 - offset));
                var end = start.AddDays(7);
                var weekJobs = jobs.Where(x => x.CreatedAt >= start && x.CreatedAt < end).ToArray();
                return new
                {
                    weekStart = start.ToString("yyyy-MM-dd"),
                    sessions = weekJobs.Select(x => SessionKey(x.PracticeSessionId, x.Id))
                        .Distinct(StringComparer.Ordinal).Count(),
                    attempts = weekJobs.Length,
                    practiceSeconds = weekJobs.Sum(x => Math.Max(0, x.DurationSeconds ?? 0)),
                    evaluatedAttempts = evaluations
                        .Where(e => string.Equals(e.Status, "Completed", StringComparison.OrdinalIgnoreCase))
                        .Select(e => e.RecordingJobId).Distinct()
                        .Count(id => weekJobs.Any(j => j.Id == id))
                };
            }).ToArray();

            var jobLookup = jobs.ToDictionary(x => x.Id);
            var latestEvaluations = evaluations
                .Where(x => string.Equals(x.Status, "Completed", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(x.Result))
                .Take(8)
                .Select(e =>
                {
                    var job = jobLookup[e.RecordingJobId];
                    return new
                    {
                        evaluationId = e.Id,
                        recordingJobId = e.RecordingJobId,
                        title = string.IsNullOrWhiteSpace(job.PracticeTitle)
                            ? "Practice — Latihan Sebelumnya" : job.PracticeTitle,
                        attemptNumber = Math.Max(1, job.PracticeAttemptNumber),
                        version = e.Version,
                        result = e.Result,
                        createdAt = e.CreatedAt,
                        completedAt = e.CompletedAt
                    };
                }).ToArray();

            return Results.Ok(new
            {
                totalSessions,
                totalAttempts = jobs.Count,
                totalPracticeSeconds = jobs.Sum(x => Math.Max(0, x.DurationSeconds ?? 0)),
                evaluatedAttempts,
                pendingEvaluations = jobs.Count(x =>
                    string.Equals(x.SpeakingCoachStatus, "Queued", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(x.SpeakingCoachStatus, "Processing", StringComparison.OrdinalIgnoreCase)),
                lastPracticeAt = jobs.FirstOrDefault()?.CreatedAt,
                weeks,
                latestEvaluations
            });
        });

        return api;
    }
}

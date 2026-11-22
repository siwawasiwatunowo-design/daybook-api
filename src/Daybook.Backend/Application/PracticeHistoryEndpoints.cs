using Daybook.Backend.Domain;
using Daybook.Backend.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Daybook.Backend.Application;

public static class PracticeHistoryEndpoints
{
    public static RouteGroupBuilder MapPracticeHistoryEndpoints(this RouteGroupBuilder api)
    {
        // The authenticated account is the only scope. Audio stays in object storage;
        // the API returns durable text/session metadata needed to rebuild Practice history.
        api.MapGet("/practice/history", async (HttpContext http, DaybookDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.FindFirst("user_id")?.Value ?? http.User.FindFirst("sub")?.Value;
            if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

            var jobs = await db.RecordingJobs.AsNoTracking()
                .Where(x => x.UserId == userId && x.EnableSpeakingCoach &&
                    (!string.IsNullOrWhiteSpace(x.PracticeSessionId) || x.PracticeTitle.StartsWith("Practice —")))
                .Include(x => x.Chunks)
                .OrderByDescending(x => x.CreatedAt)
                .ToListAsync(ct);

            var ids = jobs.Select(x => x.Id).ToArray();
            var evaluations = await db.SpeakingCoachEvaluations.AsNoTracking()
                .Where(x => x.UserId == userId && ids.Contains(x.RecordingJobId))
                .OrderBy(x => x.Version)
                .ToListAsync(ct);
            var evaluationLookup = evaluations.GroupBy(x => x.RecordingJobId)
                .ToDictionary(x => x.Key, x => x.Select(e => new
                {
                    id = e.Id,
                    version = e.Version,
                    feedback = e.Feedback,
                    status = e.Status,
                    result = e.Result,
                    structuredResultJson = e.StructuredResultJson,
                    structuredValidationStatus = e.StructuredValidationStatus,
                    error = e.Error,
                    attemptCount = e.AttemptCount,
                    createdAt = e.CreatedAt,
                    completedAt = e.CompletedAt
                }).ToArray());

            var result = jobs.Select(job => new
            {
                id = job.Id,
                activityCode = job.ActivityCode,
                status = job.Status.ToString(),
                durationSeconds = job.DurationSeconds,
                title = string.IsNullOrWhiteSpace(job.PracticeTitle)
                    ? "Practice — Latihan Sebelumnya"
                    : job.PracticeTitle,
                practiceSessionId = string.IsNullOrWhiteSpace(job.PracticeSessionId)
                    ? job.Id.ToString()
                    : job.PracticeSessionId,
                attemptNumber = Math.Max(1, job.PracticeAttemptNumber),
                practicePrompt = string.IsNullOrWhiteSpace(job.PracticePrompt)
                    ? (job.SpeakingCoachTopic ?? "")
                    : job.PracticePrompt,
                practiceMode = string.IsNullOrWhiteSpace(job.PracticeMode) ? "SOLO" : job.PracticeMode,
                sourceNoteId = job.SourceNoteId,
                createdAt = job.CreatedAt,
                updatedAt = job.UpdatedAt,
                rawTranscript = job.RawTranscript,
                refinedTranscript = job.RefinedTranscript,
                momJson = job.MomJson,
                speakingCoachStatus = job.SpeakingCoachStatus,
                speakingCoachResult = job.SpeakingCoachResult,
                speakingCoachError = job.SpeakingCoachError,
                processingStage = job.ProcessingStage,
                processingStep = job.ProcessingStep,
                processingTotalSteps = job.ProcessingTotalSteps,
                processingDetail = job.ProcessingDetail,
                chunks = job.Chunks.OrderBy(c => c.Sequence).Select(c => new
                {
                    sequence = c.Sequence,
                    status = c.Status.ToString(),
                    rawTranscript = c.RawTranscript,
                    refinedTranscript = c.RefinedTranscript,
                    sttSegmentsJson = c.SttSegmentsJson
                }).ToArray(),
                evaluations = evaluationLookup.GetValueOrDefault(job.Id) ?? []
            }).ToArray();

            return Results.Ok(result);
        });

        return api;
    }
}

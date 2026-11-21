using Daybook.Backend.Domain;
using Daybook.Backend.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Daybook.Backend.Application;

public static class SpeakingCoachEvaluationEndpoints
{
    public static RouteGroupBuilder MapSpeakingCoachEvaluationEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/recordings/{id:guid}/speaking-coach/evaluations",
            async (Guid id, CreateSpeakingCoachEvaluationRequest request, HttpContext http,
                DaybookDbContext db, CancellationToken ct) =>
            {
                var userId = http.User.FindFirst("user_id")?.Value ?? http.User.FindFirst("sub")?.Value;
                if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();
                if (request.Feedback?.Length > 2000)
                    return Results.BadRequest(new { error = "Feedback must be at most 2,000 characters." });

                var job = await db.RecordingJobs.SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
                if (job is null) return Results.NotFound();
                var transcript = string.IsNullOrWhiteSpace(job.RefinedTranscript) ? job.RawTranscript : job.RefinedTranscript;
                if (!job.EnableSpeakingCoach || string.IsNullOrWhiteSpace(transcript))
                    return Results.Conflict(new { error = "Speaking Coach is not available for this recording yet." });

                var latestVersion = await db.SpeakingCoachEvaluations
                    .Where(x => x.RecordingJobId == id).Select(x => (int?)x.Version).MaxAsync(ct) ?? 0;
                var evaluation = new SpeakingCoachEvaluation
                {
                    Id = Guid.NewGuid(),
                    RecordingJobId = id,
                    UserId = userId,
                    Version = latestVersion + 1,
                    Feedback = string.IsNullOrWhiteSpace(request.Feedback) ? null : request.Feedback.Trim(),
                    Status = "Queued",
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                db.SpeakingCoachEvaluations.Add(evaluation);
                job.SpeakingCoachStatus = "Queued";
                job.SpeakingCoachError = null;
                job.ProcessingStage = "SpeakingEvaluation";
                job.ProcessingStep = 6;
                job.ProcessingTotalSteps = 7;
                job.ProcessingDetail = $"Preparing evaluation version {evaluation.Version}";
                job.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                return Results.Accepted($"/api/recordings/{id}/speaking-coach/evaluations",
                    new { evaluation.Id, evaluation.Version, evaluation.Status });
            });

        api.MapGet("/recordings/{id:guid}/speaking-coach/evaluations",
            async (Guid id, HttpContext http, DaybookDbContext db, CancellationToken ct) =>
            {
                var userId = http.User.FindFirst("user_id")?.Value ?? http.User.FindFirst("sub")?.Value;
                if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();
                var ownsJob = await db.RecordingJobs.AnyAsync(x => x.Id == id && x.UserId == userId, ct);
                if (!ownsJob) return Results.NotFound();
                var evaluations = await db.SpeakingCoachEvaluations.AsNoTracking()
                    .Where(x => x.RecordingJobId == id && x.UserId == userId)
                    .OrderBy(x => x.Version)
                    .Select(x => new SpeakingCoachEvaluationView(x.Id, x.Version, x.Feedback, x.Status,
                        x.Result, x.Error, x.AttemptCount, x.CreatedAt, x.CompletedAt))
                    .ToListAsync(ct);
                return Results.Ok(evaluations);
            });

        return api;
    }
}

public sealed record CreateSpeakingCoachEvaluationRequest(string? Feedback);
public sealed record SpeakingCoachEvaluationView(Guid Id, int Version, string? Feedback, string Status,
    string? Result, string? Error, int AttemptCount, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

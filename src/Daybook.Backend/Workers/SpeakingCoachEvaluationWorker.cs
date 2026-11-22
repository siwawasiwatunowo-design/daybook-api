using Daybook.Backend.Application;
using Daybook.Backend.Domain;
using Daybook.Backend.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Daybook.Backend.Workers;

/// <summary>
/// Processes initial and user-requested Speaking Coach evaluations independently
/// from the audio/transcription pipeline. A Gemini outage never re-runs STT.
/// </summary>
public sealed class SpeakingCoachEvaluationWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<SpeakingCoachEvaluationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Speaking Coach evaluation worker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DaybookDbContext>();
                var now = DateTimeOffset.UtcNow;

                // Recover abandoned claims after a process restart.
                var stale = await db.SpeakingCoachEvaluations
                    .Where(x => x.Status == "Processing" && x.UpdatedAt < now.AddMinutes(-10))
                    .ToListAsync(stoppingToken);
                foreach (var item in stale)
                {
                    item.Status = item.AttemptCount >= 4 ? "Failed" : "RetryPending";
                    item.NextAttemptAt = item.Status == "RetryPending" ? now.AddMinutes(1) : null;
                    item.Error = "Recovered after evaluation worker lease expired.";
                    item.UpdatedAt = now;
                }
                if (stale.Count > 0) await db.SaveChangesAsync(stoppingToken);

                var evaluation = await db.SpeakingCoachEvaluations
                    .Where(x => x.Status == "Queued" ||
                        (x.Status == "RetryPending" && x.NextAttemptAt <= now))
                    .OrderBy(x => x.CreatedAt)
                    .FirstOrDefaultAsync(stoppingToken);

                if (evaluation is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
                    continue;
                }

                evaluation.Status = "Processing";
                evaluation.AttemptCount++;
                evaluation.UpdatedAt = now;
                await db.SaveChangesAsync(stoppingToken);

                var job = await db.RecordingJobs.SingleOrDefaultAsync(
                    x => x.Id == evaluation.RecordingJobId && x.UserId == evaluation.UserId, stoppingToken);
                if (job is null)
                {
                    evaluation.Status = "Failed";
                    evaluation.Error = "Recording was not found.";
                    evaluation.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(stoppingToken);
                    continue;
                }

                var transcript = string.IsNullOrWhiteSpace(job.RefinedTranscript)
                    ? job.RawTranscript : job.RefinedTranscript;
                if (string.IsNullOrWhiteSpace(transcript))
                {
                    evaluation.Status = "Failed";
                    evaluation.Error = "No saved transcript is available for evaluation.";
                    evaluation.UpdatedAt = DateTimeOffset.UtcNow;
                    job.SpeakingCoachStatus = "Failed";
                    job.SpeakingCoachError = evaluation.Error;
                    await db.SaveChangesAsync(stoppingToken);
                    continue;
                }

                job.SpeakingCoachStatus = "Processing";
                job.ProcessingStage = "SpeakingEvaluation";
                job.ProcessingStep = 6;
                job.ProcessingTotalSteps = 7;
                job.ProcessingDetail = $"Evaluating Speaking Coach version {evaluation.Version}";
                job.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(stoppingToken);

                try
                {
                    var evaluator = scope.ServiceProvider.GetRequiredService<ISpeakingCoachEvaluator>();
                    using var evaluationTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    evaluationTimeout.CancelAfter(TimeSpan.FromMinutes(5));
                    var rawResult = await evaluator.EvaluateAsync(transcript, job.SpeakingCoachTopic,
                        evaluationTimeout.Token, evaluation.Feedback);
                    var parsedResult = SpeakingCoachStructuredResult.Extract(rawResult, transcript);
                    var result = parsedResult.Feedback;
                    evaluation.Status = "Completed";
                    evaluation.Result = result;
                    evaluation.StructuredResultJson = parsedResult.StructuredJson;
                    evaluation.StructuredValidationStatus = parsedResult.ValidationStatus;
                    evaluation.Error = null;
                    evaluation.NextAttemptAt = null;
                    evaluation.CompletedAt = DateTimeOffset.UtcNow;
                    job.SpeakingCoachStatus = "Completed";
                    job.SpeakingCoachResult = result;
                    job.SpeakingCoachError = null;
                    job.ProcessingStage = "Completed";
                    job.ProcessingStep = 7;
                    job.ProcessingTotalSteps = 7;
                    job.ProcessingDetail = $"Evaluation version {evaluation.Version} completed";
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    evaluation.Error = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                    if (evaluation.AttemptCount < 4)
                    {
                        var delayMinutes = evaluation.AttemptCount switch { 1 => 1, 2 => 5, _ => 15 };
                        evaluation.Status = "RetryPending";
                        evaluation.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(delayMinutes);
                        job.SpeakingCoachStatus = "RetryPending";
                        job.SpeakingCoachError = evaluation.Error;
                        job.ProcessingStage = "SpeakingEvaluation";
                        job.ProcessingStep = 6;
                        job.ProcessingTotalSteps = 7;
                        job.ProcessingDetail = $"Gemini unavailable; retry scheduled in {delayMinutes} minute(s)";
                        logger.LogWarning(ex, "Speaking Coach evaluation {EvaluationId} failed; retry {Attempt}/4 scheduled.",
                            evaluation.Id, evaluation.AttemptCount);
                    }
                    else
                    {
                        evaluation.Status = "Failed";
                        evaluation.NextAttemptAt = null;
                        job.SpeakingCoachStatus = "Failed";
                        job.SpeakingCoachError = evaluation.Error;
                        job.ProcessingStage = "EvaluationNeedsAttention";
                        job.ProcessingStep = 6;
                        job.ProcessingTotalSteps = 7;
                        job.ProcessingDetail = "Automatic retries exhausted; transcript remains available";
                        logger.LogError(ex, "Speaking Coach evaluation {EvaluationId} exhausted retries.", evaluation.Id);
                    }
                }

                evaluation.UpdatedAt = DateTimeOffset.UtcNow;
                job.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(stoppingToken);

                var publisher = scope.ServiceProvider.GetRequiredService<IResultPublisher>();
                var chunks = await db.RecordingChunks.Where(x => x.RecordingJobId == job.Id)
                    .OrderBy(x => x.Sequence).ToListAsync(stoppingToken);
                await publisher.PublishAsync(job, chunks.Count, chunks.Count(x => x.Status == ChunkStatus.Completed),
                    chunks, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected Speaking Coach worker loop error.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        logger.LogInformation("Speaking Coach evaluation worker stopped.");
    }
}

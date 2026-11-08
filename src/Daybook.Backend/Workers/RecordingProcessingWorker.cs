using Daybook.Backend.Application;
using Daybook.Backend.Domain;
using Daybook.Backend.Options;
using Microsoft.Extensions.Options;

namespace Daybook.Backend.Workers;

public sealed class RecordingProcessingWorker(IServiceScopeFactory scopeFactory,
    IOptions<WorkerOptions> options, ILogger<RecordingProcessingWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Daybook recording worker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var jobs = scope.ServiceProvider.GetRequiredService<IJobRepository>();
                var id = await jobs.TryClaimNextAsync(stoppingToken);
                if (id is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.Value.PollIntervalSeconds)), stoppingToken);
                    continue;
                }

                var job = await jobs.GetAsync(id.Value, stoppingToken);
                if (job is null) continue;
                try
                {
                    var pipeline = scope.ServiceProvider.GetRequiredService<IRecordingPipeline>();
                    await pipeline.ProcessAsync(id.Value, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Processing failed for recording {RecordingId}.", id.Value);
                    job = await jobs.GetAsync(id.Value, stoppingToken) ?? job;
                    job.ErrorMessage = ex.Message;
                    job.Status = job.AttemptCount < Math.Max(1, options.Value.MaxJobAttempts)
                        ? JobStatus.Queued : JobStatus.Failed;
                    await jobs.UpdateJobAsync(job, stoppingToken);
                    var publisher = scope.ServiceProvider.GetRequiredService<IResultPublisher>();
                    var chunks = await jobs.GetChunksAsync(job.Id, stoppingToken);
                    await publisher.PublishAsync(job, chunks.Count, chunks.Count(x => x.Status == ChunkStatus.Completed), chunks, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected worker loop error.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        logger.LogInformation("Daybook recording worker stopped.");
    }
}

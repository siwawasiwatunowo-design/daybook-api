using Daybook.Backend.Application;

namespace Daybook.Backend.Infrastructure;

// V1 uses the persisted PostgreSQL job row as its queue. The hosted worker polls it,
// so this dispatcher is intentionally a no-op. Keep this seam so V2 can replace it
// with Hangfire without changing API endpoints or the transcription pipeline.
public sealed class DatabasePollingJobDispatcher(ILogger<DatabasePollingJobDispatcher> logger)
    : IJobDispatcher
{
    public Task EnqueueAsync(Guid recordingId, CancellationToken ct)
    {
        logger.LogDebug("Recording job {RecordingId} persisted; PostgreSQL polling worker will pick it up.", recordingId);
        return Task.CompletedTask;
    }
}

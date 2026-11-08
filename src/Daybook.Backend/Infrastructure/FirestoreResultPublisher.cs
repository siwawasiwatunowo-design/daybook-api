using Daybook.Backend.Application;
using Daybook.Backend.Domain;
using Google.Cloud.Firestore;

namespace Daybook.Backend.Infrastructure;

/// <summary>Firestore is a client-facing read model; PostgreSQL remains the backend source of truth.</summary>
public sealed class FirestoreResultPublisher(string projectId, ILogger<FirestoreResultPublisher> logger)
    : IResultPublisher
{
    private readonly FirestoreDb _db = FirestoreDb.Create(projectId);

    public async Task PublishAsync(RecordingJob job, int chunkCount, int completedChunks, IReadOnlyList<RecordingChunk> changedChunks, CancellationToken ct)
    {
        try
        {
            var userDoc = _db.Collection("users").Document(job.UserId);
            var recordingDoc = userDoc.Collection("recordings").Document(job.Id.ToString());
            var root = new Dictionary<string, object>
            {
                ["recordingId"] = job.Id.ToString(),
                ["activityCode"] = job.ActivityCode,
                ["status"] = job.Status.ToString(),
                ["durationSeconds"] = job.DurationSeconds ?? 0,
                ["chunkCount"] = chunkCount,
                ["completedChunks"] = completedChunks,
                ["errorMessage"] = job.ErrorMessage ?? "",
                ["profileVersion"] = job.ProfileVersion,
                ["rawTranscriptAvailable"] = !string.IsNullOrWhiteSpace(job.RawTranscript),
                ["refinedTranscriptAvailable"] = !string.IsNullOrWhiteSpace(job.RefinedTranscript),
                ["momAvailable"] = !string.IsNullOrWhiteSpace(job.MomJson),
                ["createdAt"] = Timestamp.FromDateTime(job.CreatedAt.UtcDateTime),
                ["updatedAt"] = Timestamp.FromDateTime(DateTime.UtcNow)
            };
            await recordingDoc.SetAsync(root, SetOptions.MergeAll, ct);

            // Keep transcript pieces in a subcollection so a long recording cannot exceed Firestore's 1 MiB document limit.
            foreach (var chunk in changedChunks)
            {
                var chunkDoc = recordingDoc.Collection("transcriptChunks").Document(chunk.Sequence.ToString("D5"));
                var data = new Dictionary<string, object>
                {
                    ["sequence"] = chunk.Sequence,
                    ["status"] = chunk.Status.ToString(),
                    ["rawTranscript"] = chunk.RawTranscript ?? "",
                    ["refinedTranscript"] = chunk.RefinedTranscript ?? "",
                    ["selectedSttModel"] = chunk.SelectedSttModel ?? "",
                    ["qualityMetricsJson"] = chunk.QualityMetricsJson ?? "",
                    ["sttAttemptsJson"] = chunk.SttAttemptsJson ?? "",
                    ["sttSegmentsJson"] = chunk.SttSegmentsJson ?? "",
                    ["errorMessage"] = chunk.ErrorMessage ?? "",
                    ["updatedAt"] = Timestamp.FromDateTime(DateTime.UtcNow)
                };
                await chunkDoc.SetAsync(data, SetOptions.MergeAll, ct);
            }
            var momDoc = recordingDoc.Collection("outputs").Document("mom");
            if (!string.IsNullOrWhiteSpace(job.MomJson))
                await momDoc.SetAsync(
                    new Dictionary<string, object> { ["momJson"] = job.MomJson, ["updatedAt"] = Timestamp.FromDateTime(DateTime.UtcNow) },
                    SetOptions.MergeAll, ct);
            else
                await momDoc.DeleteAsync(cancellationToken: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Firestore is a mirror for Android. A temporary mirror outage must not erase or fail PostgreSQL processing.
            logger.LogError(ex, "Could not publish recording {RecordingId} to Firestore read model.", job.Id);
        }
    }
}

public sealed class NoOpResultPublisher : IResultPublisher
{
    public Task PublishAsync(RecordingJob job, int chunkCount, int completedChunks, IReadOnlyList<RecordingChunk> changedChunks, CancellationToken ct) => Task.CompletedTask;
}

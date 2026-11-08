using Daybook.Backend.Domain;
using Daybook.Backend.Options;

namespace Daybook.Backend.Application;

public sealed record CreateRecordingRequest(string ActivityCode, string AudioObjectName, int? DurationSeconds = null, string? LanguageOverride = null, string? ClientRecordingId = null);

public interface IJobRepository
{
    Task<RecordingJob> CreateAsync(string userId, CreateRecordingRequest request, ActivityProcessingProfile profile, CancellationToken ct);
    Task<RecordingJobView?> GetForUserAsync(Guid id, string userId, CancellationToken ct);
    Task<bool> RequestRetryAsync(Guid id, string userId, CancellationToken ct);
    Task<Guid?> TryClaimNextAsync(CancellationToken ct);
    Task<RecordingJob?> GetAsync(Guid id, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
    Task EnsureChunksAsync(Guid jobId, IReadOnlyList<string> chunkNames, CancellationToken ct);
    Task<List<RecordingChunk>> GetChunksAsync(Guid jobId, CancellationToken ct);
    Task UpdateChunkAsync(RecordingChunk chunk, CancellationToken ct);
    Task UpdateJobAsync(RecordingJob job, CancellationToken ct);
}

public interface IActivityProfileProvider
{
    Task<bool> ExistsAsync(string activityCode, CancellationToken ct);
    Task<ActivityProcessingProfile> GetAsync(string activityCode, CancellationToken ct);
    Task<IReadOnlyList<ActivityProcessingProfile>> GetAllAsync(CancellationToken ct);
}

public interface IRecordingObjectStore
{
    Task DownloadToFileAsync(string objectName, string destinationPath, CancellationToken ct);
}

public sealed record RecordingUploadUrl(string UploadUrl, string AudioObjectName,
    DateTimeOffset ExpiresAt, string ContentType);

public interface IRecordingUploadUrlProvider
{
    Task<RecordingUploadUrl> CreateUploadUrlAsync(string userId, string clientRecordingId,
        string contentType, CancellationToken ct);
}

public interface IAudioChunker
{
    Task<IReadOnlyList<string>> CreateChunksAsync(string inputPath, string outputDirectory,
        ActivityProcessingProfile profile, CancellationToken ct);
}

public sealed record SttSegmentResult(string Text, double? AverageLogProbability,
    double? NoSpeechProbability, double? CompressionRatio, double? StartSeconds, double? EndSeconds);
public sealed record SttTranscriptionResult(string Text, string Model, string? DetectedLanguage,
    double? LanguageProbability, double? DurationSeconds, IReadOnlyList<SttSegmentResult> Segments);

public interface ISpeechToTextProvider
{
    Task<SttTranscriptionResult> TranscribeAsync(string audioPath, ActivityProcessingProfile profile,
        string model, string language, CancellationToken ct);
}

public sealed record TranscriptQualityResult(bool IsSuspicious, string[] Reasons,
    double RepeatedWordRatio, double RepeatedNgramRatio, double WordsPerMinute,
    double? AverageLogProbability, double? NoSpeechProbability, double? CompressionRatio,
    double? LanguageProbability, double DiagnosticScore);
public interface ITranscriptQualityChecker
{
    TranscriptQualityResult Check(SttTranscriptionResult result, ActivityProcessingProfile profile);
}

public sealed record RefinedChunkResult(string RefinedTranscript, string ChunkSummaryJson);
public interface ITranscriptRefiner
{
    Task<RefinedChunkResult> RefineChunkAsync(string rawTranscript, ActivityProcessingProfile profile, CancellationToken ct);
    Task<string> GenerateFinalMomAsync(IReadOnlyList<string> chunkSummaries, ActivityProcessingProfile profile, CancellationToken ct);
}

public interface IJobDispatcher
{
    Task EnqueueAsync(Guid recordingId, CancellationToken ct);
}

public interface IResultPublisher
{
    Task PublishAsync(RecordingJob job, int chunkCount, int completedChunks, IReadOnlyList<RecordingChunk> changedChunks, CancellationToken ct);
}

public interface IRecordingPipeline
{
    Task ProcessAsync(Guid recordingId, CancellationToken ct);
}

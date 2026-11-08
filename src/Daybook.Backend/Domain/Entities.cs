namespace Daybook.Backend.Domain;

public enum JobStatus { Queued, Processing, NeedsReview, Completed, Failed }
public enum ChunkStatus { Pending, Processing, Completed, Failed }

public sealed class RecordingJob
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string ActivityCode { get; set; } = "";
    public string AudioObjectName { get; set; } = "";
    public string? ClientRecordingId { get; set; }
    public int? DurationSeconds { get; set; }
    public string? LanguageOverride { get; set; }
    public string ProfileVersion { get; set; } = "1";
    public string? ProfileSnapshotJson { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public int AttemptCount { get; set; }
    public string? ErrorMessage { get; set; }
    public string? RawTranscript { get; set; }
    public string? RefinedTranscript { get; set; }
    public string? MomJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public List<RecordingChunk> Chunks { get; set; } = [];
}

public sealed class RecordingChunk
{
    public Guid Id { get; set; }
    public Guid RecordingJobId { get; set; }
    public int Sequence { get; set; }
    public string AudioFileName { get; set; } = "";
    public ChunkStatus Status { get; set; } = ChunkStatus.Pending;
    public int AttemptCount { get; set; }
    public string? RawTranscript { get; set; }
    public string? RefinedTranscript { get; set; }
    public string? ChunkSummaryJson { get; set; }
    public string? SelectedSttModel { get; set; }
    public string? QualityMetricsJson { get; set; }
    public string? SttAttemptsJson { get; set; }
    public string? SttSegmentsJson { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TranscriptionBenchmark
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string ActivityCode { get; set; } = "";
    public string AudioObjectName { get; set; } = "";
    public string ReferenceTranscript { get; set; } = "";
    public string ResultsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record RecordingChunkView(int Sequence, ChunkStatus Status, int AttemptCount,
    string? SelectedSttModel, string? QualityMetricsJson, string? SttAttemptsJson,
    string? SttSegmentsJson, string? ErrorMessage, double StartSeconds = 0);
public sealed record RecordingJobView(Guid Id, string ActivityCode, JobStatus Status,
    int? DurationSeconds, int ChunkCount, int CompletedChunks, string? ErrorMessage,
    string? RawTranscript, string? RefinedTranscript, string? MomJson,
    IReadOnlyList<RecordingChunkView> Chunks, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

namespace Daybook.Backend.Domain;

public enum JobStatus { Queued, Processing, NeedsReview, Completed, Failed }
public enum ChunkStatus { Pending, Processing, Completed, Failed }

public sealed class RecordingJob
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string ActivityCode { get; set; } = "";
    public string AudioObjectName { get; set; } = "";
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
    public bool EnableSpeakingCoach { get; set; }
    public string? SpeakingCoachTopic { get; set; }
    public string PracticeTitle { get; set; } = "";
    public string PracticeSessionId { get; set; } = "";
    public int PracticeAttemptNumber { get; set; } = 1;
    public string PracticePrompt { get; set; } = "";
    public string PracticeMode { get; set; } = "SOLO";
    public string SourceNoteId { get; set; } = "";
    public string SpeakingCoachStatus { get; set; } = "NotRequested";
    public string? SpeakingCoachResult { get; set; }
    public string? SpeakingCoachError { get; set; }
    public string ProcessingStage { get; set; } = "Queued";
    public int ProcessingStep { get; set; } = 1;
    public int ProcessingTotalSteps { get; set; } = 7;
    public string? ProcessingDetail { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public List<RecordingChunk> Chunks { get; set; } = [];
}

public sealed class SpeakingCoachEvaluation
{
    public Guid Id { get; set; }
    public Guid RecordingJobId { get; set; }
    public string UserId { get; set; } = "";
    public int Version { get; set; }
    public string? Feedback { get; set; }
    public string Status { get; set; } = "Queued";
    public string? Result { get; set; }
    public string? Error { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
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
    string? SttSegmentsJson, string? ErrorMessage);
public sealed record RecordingJobView(Guid Id, string ActivityCode, JobStatus Status,
    int? DurationSeconds, int ChunkCount, int CompletedChunks, string? ErrorMessage,
    string? RawTranscript, string? RefinedTranscript, string? MomJson,
    IReadOnlyList<RecordingChunkView> Chunks, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    bool EnableSpeakingCoach, string SpeakingCoachStatus, string? SpeakingCoachResult, string? SpeakingCoachError,
    string ProcessingStage, int ProcessingStep, int ProcessingTotalSteps, string? ProcessingDetail);

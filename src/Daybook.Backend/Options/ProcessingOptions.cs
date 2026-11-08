namespace Daybook.Backend.Options;

public sealed class WorkerOptions
{
    public int PollIntervalSeconds { get; set; } = 3;
    public int MaxJobAttempts { get; set; } = 3;
    public int MaxConcurrentJobs { get; set; } = 1;
    public string TempDirectory { get; set; } = "./data/tmp";
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
}

public sealed class StorageOptions
{
    public string Provider { get; set; } = "NeonS3";
    public string Endpoint { get; set; } = "";
    public string Region { get; set; } = "us-east-2";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string Bucket { get; set; } = "";
}

public sealed class SttOptions
{
    public string Provider { get; set; } = "Groq";
    public string Endpoint { get; set; } = "https://api.groq.com/openai/v1/audio/transcriptions";
    public string ApiKey { get; set; } = "";
    public string PrimaryModel { get; set; } = "whisper-large-v3";
    public string FallbackModel { get; set; } = "whisper-large-v3-turbo";
    public int TimeoutMinutes { get; set; } = 10;
    public int MaxUploadMegabytes { get; set; } = 24;
}

public sealed class GeminiOptions
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gemini-2.5-flash";
    public string EndpointBase { get; set; } = "https://generativelanguage.googleapis.com/v1beta/models";
    public int TimeoutMinutes { get; set; } = 5;
}

public sealed class ActivityProfileOptions
{
    public List<ActivityProcessingProfile> Profiles { get; set; } = [];
}

public sealed class ActivityProcessingProfile
{
    public string ActivityCode { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Version { get; set; } = "1";
    public string Language { get; set; } = "id";
    public string? PrimarySttModel { get; set; }
    public string? FallbackSttModel { get; set; }
    public List<string> AlternativeSttModels { get; set; } = [];
    public int ChunkDurationMinutes { get; set; } = 10;
    public double ChunkOverlapSeconds { get; set; } = 1.5;
    public string PreprocessingProfile { get; set; } = "standard";
    public string VocabularyInstructions { get; set; } = "";
    public bool QualityCheckEnabled { get; set; } = true;
    public double RepeatedWordRatioThreshold { get; set; } = 0.12;
    public bool RetryWithFallbackModel { get; set; } = true;
    public string RefinementProfile { get; set; } = "general";
    public bool GenerateMom { get; set; } = true;
    public int BeamSize { get; set; } = 5;
    public int BestOf { get; set; } = 5;
    public double Temperature { get; set; } = 0.0;
    public bool VadFilter { get; set; } = true;
    public int VadMinSilenceMs { get; set; } = 500;
    public int VadSpeechPadMs { get; set; } = 250;
    public bool ConditionOnPreviousText { get; set; } = false;
    public double NoSpeechProbabilityThreshold { get; set; } = 0.6;
    public double AverageLogProbabilityThreshold { get; set; } = -1.0;
    public double CompressionRatioThreshold { get; set; } = 2.4;
    public double MinLanguageProbability { get; set; } = 0.55;
    public double MinSpeechWordsPerMinute { get; set; } = 8;
    public double MaxRepeatedNgramRatio { get; set; } = 0.15;
    public int MaxRetryModels { get; set; } = 1;
    public List<string> VocabularyTerms { get; set; } = [];
    public List<string> VocabularyAliases { get; set; } = [];
    public bool EnableGeminiRefinement { get; set; } = true;
    public bool PreserveUncertainTerms { get; set; } = true;
    public int HighPassHz { get; set; } = 80;
    public int LowPassHz { get; set; } = 7600;
    public bool EnableDenoise { get; set; } = false;
    public int DenoiseNoiseFloorDb { get; set; } = 25;
    public double LoudnessTargetI { get; set; } = -16;
}

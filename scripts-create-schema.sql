CREATE TABLE IF NOT EXISTS "RecordingJobs" (
  "Id" uuid PRIMARY KEY,
  "UserId" varchar(128) NOT NULL,
  "ActivityCode" varchar(80) NOT NULL,
  "AudioObjectName" varchar(1024) NOT NULL,
  "DurationSeconds" integer NULL,
  "LanguageOverride" varchar(32) NULL,
  "ProfileVersion" varchar(40) NOT NULL,
  "ProfileSnapshotJson" text NULL,
  "Status" integer NOT NULL,
  "AttemptCount" integer NOT NULL,
  "ErrorMessage" text NULL,
  "RawTranscript" text NULL,
  "RefinedTranscript" text NULL,
  "MomJson" text NULL,
  "EnableSpeakingCoach" boolean NOT NULL DEFAULT false,
  "SpeakingCoachTopic" text NULL,
  "SpeakingCoachStatus" varchar(32) NOT NULL DEFAULT 'NotRequested',
  "SpeakingCoachResult" text NULL,
  "SpeakingCoachError" text NULL,
  "CreatedAt" timestamptz NOT NULL,
  "UpdatedAt" timestamptz NOT NULL,
  "CompletedAt" timestamptz NULL
);
CREATE INDEX IF NOT EXISTS "IX_RecordingJobs_Status_CreatedAt" ON "RecordingJobs" ("Status", "CreatedAt");
CREATE INDEX IF NOT EXISTS "IX_RecordingJobs_UserId_CreatedAt" ON "RecordingJobs" ("UserId", "CreatedAt");

CREATE TABLE IF NOT EXISTS "RecordingChunks" (
  "Id" uuid PRIMARY KEY,
  "RecordingJobId" uuid NOT NULL REFERENCES "RecordingJobs"("Id") ON DELETE CASCADE,
  "Sequence" integer NOT NULL,
  "AudioFileName" text NOT NULL,
  "Status" integer NOT NULL,
  "AttemptCount" integer NOT NULL,
  "RawTranscript" text NULL,
  "RefinedTranscript" text NULL,
  "ChunkSummaryJson" text NULL,
  "SelectedSttModel" varchar(100) NULL,
  "QualityMetricsJson" text NULL,
  "SttAttemptsJson" text NULL,
  "SttSegmentsJson" text NULL,
  "ErrorMessage" text NULL,
  "UpdatedAt" timestamptz NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_RecordingChunks_RecordingJobId_Sequence" ON "RecordingChunks" ("RecordingJobId", "Sequence");

CREATE TABLE IF NOT EXISTS "TranscriptionBenchmarks" (
  "Id" uuid PRIMARY KEY,
  "UserId" varchar(128) NOT NULL,
  "ActivityCode" varchar(80) NOT NULL,
  "AudioObjectName" varchar(1024) NOT NULL,
  "ReferenceTranscript" text NOT NULL,
  "ResultsJson" text NOT NULL,
  "CreatedAt" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_TranscriptionBenchmarks_UserId_CreatedAt" ON "TranscriptionBenchmarks" ("UserId", "CreatedAt");

-- Safe additive upgrades for databases created by earlier V1 starter builds.
ALTER TABLE "RecordingChunks" ADD COLUMN IF NOT EXISTS "SelectedSttModel" varchar(100) NULL;
ALTER TABLE "RecordingChunks" ADD COLUMN IF NOT EXISTS "QualityMetricsJson" text NULL;
ALTER TABLE "RecordingChunks" ADD COLUMN IF NOT EXISTS "SttAttemptsJson" text NULL;
ALTER TABLE "RecordingChunks" ADD COLUMN IF NOT EXISTS "SttSegmentsJson" text NULL;

-- Optional Speaking Coach extension on the existing RecordingJobs pipeline.
ALTER TABLE "RecordingJobs" ADD COLUMN IF NOT EXISTS "EnableSpeakingCoach" boolean NOT NULL DEFAULT false;
ALTER TABLE "RecordingJobs" ADD COLUMN IF NOT EXISTS "SpeakingCoachTopic" text NULL;
ALTER TABLE "RecordingJobs" ADD COLUMN IF NOT EXISTS "SpeakingCoachStatus" varchar(32) NOT NULL DEFAULT 'NotRequested';
ALTER TABLE "RecordingJobs" ADD COLUMN IF NOT EXISTS "SpeakingCoachResult" text NULL;
ALTER TABLE "RecordingJobs" ADD COLUMN IF NOT EXISTS "SpeakingCoachError" text NULL;


-- Detailed processing progress, used by the Android recording timeline.
ALTER TABLE "RecordingJobs" ADD COLUMN IF NOT EXISTS "ProcessingStage" varchar(64) NOT NULL DEFAULT 'AudioUploaded';
ALTER TABLE "RecordingJobs" ADD COLUMN IF NOT EXISTS "ProcessingStep" integer NOT NULL DEFAULT 1;
ALTER TABLE "RecordingJobs" ADD COLUMN IF NOT EXISTS "ProcessingTotalSteps" integer NOT NULL DEFAULT 7;
ALTER TABLE "RecordingJobs" ADD COLUMN IF NOT EXISTS "ProcessingDetail" text NULL;

-- Durable Speaking Coach evaluation history and retry queue. Each manual re-evaluation
-- creates a new version; previous results are never overwritten.
CREATE TABLE IF NOT EXISTS "SpeakingCoachEvaluations" (
  "Id" uuid PRIMARY KEY,
  "RecordingJobId" uuid NOT NULL REFERENCES "RecordingJobs"("Id") ON DELETE CASCADE,
  "UserId" varchar(128) NOT NULL,
  "Version" integer NOT NULL,
  "Feedback" text NULL,
  "Status" varchar(32) NOT NULL DEFAULT 'Queued',
  "Result" text NULL,
  "Error" text NULL,
  "AttemptCount" integer NOT NULL DEFAULT 0,
  "NextAttemptAt" timestamptz NULL,
  "CreatedAt" timestamptz NOT NULL,
  "UpdatedAt" timestamptz NOT NULL,
  "CompletedAt" timestamptz NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_SpeakingCoachEvaluations_RecordingJobId_Version"
  ON "SpeakingCoachEvaluations" ("RecordingJobId", "Version");
CREATE INDEX IF NOT EXISTS "IX_SpeakingCoachEvaluations_Status_NextAttemptAt_CreatedAt"
  ON "SpeakingCoachEvaluations" ("Status", "NextAttemptAt", "CreatedAt");

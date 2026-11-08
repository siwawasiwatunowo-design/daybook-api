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


-- Additive upgrades: stable client-side idempotency and replay-safe job creation.
ALTER TABLE "RecordingJobs" ADD COLUMN IF NOT EXISTS "ClientRecordingId" varchar(64) NULL;
CREATE UNIQUE INDEX IF NOT EXISTS "IX_RecordingJobs_UserId_ClientRecordingId"
  ON "RecordingJobs" ("UserId", "ClientRecordingId") WHERE "ClientRecordingId" IS NOT NULL;

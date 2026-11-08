# Architecture — Free-tier Daybook V1

```text
Android app
  | Firebase ID token
  | POST /api/recordings/upload-url
  v
Local .NET 10 API + BackgroundService (Windows 10 laptop; no Docker)
  |-- Neon PostgreSQL Free: jobs, chunks, transcripts, checkpoints
  |-- Neon Object Storage Free: original audio via S3 presigned PUT URL
  |-- Groq Whisper API Free: speech-to-text per 8-minute chunk
  |-- Gemini Developer API Free: optional refinement and MoM
  `-- Firebase Firestore: optional result read model
```

## Core decisions

1. No Docker, local PostgreSQL, or local Whisper model is required for this setup.
2. Neon PostgreSQL is the source of truth. Neon Free currently provides 1 GB DB storage and 100 CU-hours per project/month; compute scales to zero after inactivity.
3. Neon Object Storage is S3-compatible and provides 5 GB per project on Free. Create a private bucket and a scoped credential with `storage:read` and `storage:write`. Use Singapore (`ap-southeast-1`) where available.
4. Firebase Auth remains the token issuer. Firestore is an optional read model within its free quotas. Firebase Storage is not used because Firebase requires Blaze billing for Cloud Storage access.
5. Groq hosts Whisper Large V3 / Large V3 Turbo. Free API access is rate-limited and the free audio file limit is 25 MB. Chunking is set to 8 minutes, PCM 16 kHz mono, approximately 15.4 MB/chunk.
6. Gemini free API is optional and quota-limited. Free-tier content handling may permit use to improve Google products. Do not process confidential recordings without reviewing terms.
7. API + worker run on the Windows laptop. The worker is unavailable while the laptop is off/asleep. This is not a hosted always-on backend.

## 12-hour recordings

A 12-hour file is split into 8-minute chunks. Each completed chunk is checkpointed in PostgreSQL, so a retry does not re-transcribe completed chunks. Groq free-tier audio quotas may prevent the full 12 hours from finishing in one day. On HTTP 429, wait for the provider quota reset, then retry the recording. Do not loop retries while quota is exhausted.

## Android upload sequence

1. Authenticate with Firebase and obtain Firebase ID token.
2. Request `POST /api/recordings/upload-url` with a GUID and content type.
3. Upload bytes with HTTP PUT to the presigned Neon URL, preserving returned Content-Type.
4. Call `POST /api/recordings` with the returned `audioObjectName`.
5. Poll API or listen to Firestore read model.

The old Firebase Storage SDK upload path must be replaced in Android.

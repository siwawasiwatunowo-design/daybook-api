# API Contract — Free-tier V1

All `/api/*` routes require `Authorization: Bearer <Firebase ID token>`.

## `POST /api/recordings/upload-url`

Request:

```json
{
  "clientRecordingId": "8d8a0bd0-08b4-4f84-9e8f-2c2f1d1a2c44",
  "contentType": "audio/wav"
}
```

`clientRecordingId` must be a GUID. Allowed content types: `audio/wav`, `audio/x-wav`, `audio/mpeg`, `audio/mp4`, `audio/aac`, `audio/flac`, `audio/ogg`.

Response: `uploadUrl`, `audioObjectName`, `expiresAt`, `contentType`. PUT the file bytes to `uploadUrl` within 15 minutes and send the exact returned `Content-Type`. The object key is scoped to the authenticated Firebase UID.

## `POST /api/recordings`

Call only after the object upload succeeds.

```json
{
  "activityCode": "GENERAL_MEETING",
  "audioObjectName": "users/FIREBASE_UID/recordings/GUID/original",
  "durationSeconds": 367,
  "languageOverride": "id"
}
```

Returns HTTP 202 with job ID/status. The server verifies that `audioObjectName` starts with the authenticated user's storage prefix.

## Other routes

- `GET /health` — public health check.
- `GET /api/activity-profiles` — profile configuration.
- `GET /api/recordings/{id}` — own job status/result.
- `POST /api/recordings/{id}/retry` — retry failed or review-required job.
- `POST /api/benchmarks/transcription` — benchmark `whisper-large-v3` and/or `whisper-large-v3-turbo` against a manually verified reference transcript.

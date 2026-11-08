# Daybook Backend V1 — Free-tier development setup

This revision targets **Windows 10 + 8 GB RAM, no Docker, and $0/month provider plans within their published free quotas**. The .NET API and BackgroundService run on the laptop. PostgreSQL and audio objects use Neon Free; STT uses Groq's free API tier; Gemini uses its free API tier; Firebase Authentication and Firestore use their no-cost offerings.

> Important: Firebase Cloud Storage is deliberately not used. Firebase now requires the Blaze billing plan to access Storage buckets. Audio upload uses Neon Object Storage with S3 presigned URLs, so the Android upload implementation must be changed accordingly.

## Start here

1. Follow [`docs/FREE-SETUP-WINDOWS.md`](docs/FREE-SETUP-WINDOWS.md) in order.
2. Install .NET 10 SDK and FFmpeg/FFprobe.
3. Create Neon Free database + private `daybook-audio` Object Storage bucket in Singapore.
4. Create Groq and Gemini API keys; configure Firebase Auth + Firestore and service-account credentials.
5. Set secrets using .NET User Secrets.
6. Run `dotnet restore`, `dotnet build`, then `run-local.ps1`.
7. Import Postman collection and environment from `postman/`.

## Architecture

- .NET 10 Minimal API + PostgreSQL polling BackgroundService (same process, local laptop).
- Neon PostgreSQL is the authoritative job/transcript database.
- Neon Object Storage stores original audio; backend creates short-lived presigned upload URLs.
- Firebase ID tokens authenticate the Android app; Firestore is an optional client-facing result mirror.
- Groq Whisper API transcribes chunked audio; Gemini free API optionally refines transcript and creates MoM.
- 8-minute 16 kHz mono WAV chunks keep individual STT requests under the free-tier 25 MB file limit with a safety margin.

## API endpoints

- `GET /health`
- `GET /api/activity-profiles`
- `POST /api/recordings/upload-url` — issue a 15-minute presigned Neon Object Storage PUT URL.
- `POST /api/recordings` — create a processing job after upload succeeds.
- `GET /api/recordings/{id}`
- `POST /api/recordings/{id}/retry`
- `POST /api/benchmarks/transcription` — compare supported Groq Whisper models against a human-verified transcript.

## Important limits

This is a free-tier development setup, not unlimited hosting. Neon, Firestore, Groq and Gemini all have finite quotas. A 12-hour recording can exceed Groq's daily free audio quota and may need a later retry; completed chunks are checkpointed. The local worker stops when the laptop is off. Free Gemini data handling may allow submitted content to be used to improve Google products; do not submit confidential meeting audio unless the terms are acceptable.

See [`docs/FREE-SETUP-WINDOWS.md`](docs/FREE-SETUP-WINDOWS.md), [`docs/API-CONTRACT.md`](docs/API-CONTRACT.md), [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md), and [`docs/ACCURACY-TEST-PLAN.md`](docs/ACCURACY-TEST-PLAN.md).

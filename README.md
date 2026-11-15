# Daybook Backend V1 — Local Development Setup

This repository currently documents the **local development deployment**. The .NET API and background worker run on the Windows laptop. PostgreSQL and audio objects are hosted by Neon; Firebase, Groq, and Gemini are external services. This is **not yet a production/always-on backend**.

> **Important:** keep credentials in .NET User Secrets or environment variables. Never commit API keys, database passwords, S3 credentials, Firebase service-account JSON, or real connection strings.

## Current deployment (local)

| Component | Current setup |
|---|---|
| Backend API + worker | .NET 10 Minimal API + BackgroundService on Windows laptop |
| API local URL | `http://localhost:5000` when using the current local launch script; confirm the port in the script before changing it |
| Database | Neon PostgreSQL (cloud-hosted) |
| Audio storage | Neon Object Storage, S3-compatible, private bucket `daybook-audio` |
| Authentication | Firebase Authentication; Firebase project `daybook-53705` |
| Result mirror | Cloud Firestore (enabled in appsettings) |
| Speech-to-text | Groq API: `whisper-large-v3`, fallback `whisper-large-v3-turbo` |
| Transcript refinement + MoM | Gemini Developer API |
| Android access during local development | ngrok HTTPS tunnel; Android build/config uses `DAYBOOK_API_BASE_URL` |

The laptop must remain powered on, awake, and connected to the internet while the local worker processes recording jobs. If the laptop or local API stops, background processing stops. Neon/Firebase/Groq/Gemini are cloud services; only the .NET API/worker is local.

### Current Gemini model setting (verified 15 Nov 2026)

A direct `generateContent` test using the configured Gemini API key returned:

- `gemini-3.5-flash-lite`: **SUCCESS**
- `gemini-3.8-flash`: **503 Server Unavailable** at test time

Therefore, set the local User Secret so the tested model is primary:

```powershell
dotnet user-secrets set "Gemini:Model" "gemini-3.5-flash-lite" --project ".\\src\\Daybook.Backend\\Daybook.Backend.csproj"
```

The tracked appsettings currently lists `gemini-3.8-flash` as Model and `gemini-3.5-flash-lite` as FallbackModel. **User Secrets override appsettings**, so the command above is important on this development machine. Restart the backend after changing secrets. The 503 result is time-specific; retest model availability before changing models later. Do not assume model listing alone guarantees successful generation.

Current Gemini request settings in appsettings:
- Endpoint: `https://generativelanguage.googleapis.com/v1beta/models`
- Per-request timeout: `18` seconds (`Gemini:RequestTimeoutSeconds`)
- Overall configured timeout: `5` minutes (`Gemini:TimeoutMinutes`)
- Refinement and MoM have separate pipeline-level time budgets; see the processing implementation before changing timeout values.

## Prerequisites

| Tool | Version | Install |
|---|---|---|
| .NET SDK | 10+ | [dot.net/download](https://dotnet.microsoft.com/download) |
| FFmpeg + FFprobe | latest | `scoop install ffmpeg` or `winget install Gyan.FFmpeg` |

Scoop users: FFmpeg is usually under `%USERPROFILE%\\scoop\\shims\\`. The local launcher should add this directory to PATH or use explicit executable paths.

## Configure local secrets

Run commands from the repository root. Initialize User Secrets once:

```powershell
dotnet user-secrets init --project ".\\src\\Daybook.Backend\\Daybook.Backend.csproj"
```

The application uses these secret/configuration keys (set values from the provider dashboards; do not put actual values in this README):

| Key | Purpose |
|---|---|
| `ConnectionStrings:Postgres` | Neon PostgreSQL connection string |
| `Storage:Endpoint` | Neon S3-compatible endpoint |
| `Storage:Region` | Usually `ap-southeast-1` for the current Singapore bucket |
| `Storage:Bucket` | Current bucket: `daybook-audio` |
| `Storage:AccessKey` | Neon S3 access key ID |
| `Storage:SecretKey` | Neon S3 secret access key |
| `Stt:ApiKey` | Groq API key |
| `Gemini:ApiKey` | Google AI Studio / Gemini API key |
| `Gemini:Model` | Primary Gemini model; currently set locally to `gemini-3.5-flash-lite` |
| `Gemini:FallbackModel` | Secondary Gemini model from appsettings; retest before relying on it |
| `Firebase:ProjectId` | `daybook-53705` (non-secret; configured in appsettings) |
| `GOOGLE_APPLICATION_CREDENTIALS` | Environment variable pointing to Firebase service-account JSON outside the repo |

Example commands (replace placeholders locally; never paste real values into source control):

```powershell
dotnet user-secrets set "ConnectionStrings:Postgres" "YOUR_NEON_POSTGRES_CONNECTION_STRING" --project ".\\src\\Daybook.Backend\\Daybook.Backend.csproj"
dotnet user-secrets set "Storage:Endpoint" "YOUR_NEON_S3_ENDPOINT" --project ".\\src\\Daybook.Backend\\Daybook.Backend.csproj"
dotnet user-secrets set "Storage:Region" "ap-southeast-1" --project ".\\src\\Daybook.Backend\\Daybook.Backend.csproj"
dotnet user-secrets set "Storage:Bucket" "daybook-audio" --project ".\\src\\Daybook.Backend\\Daybook.Backend.csproj"
dotnet user-secrets set "Storage:AccessKey" "YOUR_NEON_S3_ACCESS_KEY" --project ".\\src\\Daybook.Backend\\Daybook.Backend.csproj"
dotnet user-secrets set "Storage:SecretKey" "YOUR_NEON_S3_SECRET_KEY" --project ".\\src\\Daybook.Backend\\Daybook.Backend.csproj"
dotnet user-secrets set "Stt:ApiKey" "YOUR_GROQ_API_KEY" --project ".\\src\\Daybook.Backend\\Daybook.Backend.csproj"
dotnet user-secrets set "Gemini:ApiKey" "YOUR_GEMINI_API_KEY" --project ".\\src\\Daybook.Backend\\Daybook.Backend.csproj"
dotnet user-secrets set "Gemini:Model" "gemini-3.5-flash-lite" --project ".\\src\\Daybook.Backend\\Daybook.Backend.csproj"
```

Set Firebase credentials in the current PowerShell session (change to the actual local file path):

```powershell
$env:GOOGLE_APPLICATION_CREDENTIALS = "C:\\Secure\\daybook-firebase-service-account.json"
```

The service-account JSON must stay outside the repository. To inspect configured secret names locally, use `dotnet user-secrets list --project ".\\src\\Daybook.Backend\\Daybook.Backend.csproj"`; **do not share its output publicly**, because it prints secret values.

## Run locally

Use the local launcher script that exists in your working copy. Check its contents for the configured port, environment variables, FFmpeg paths, and Firebase credential path before running it. If running manually:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project ".\\src\\Daybook.Backend\\Daybook.Backend.csproj" --no-launch-profile --urls "http://localhost:5000"
```

The port above is an example for the current local setup; keep it aligned with the launcher and Android tunnel configuration.

Health check:

```powershell
Invoke-WebRequest http://localhost:5000/health -UseBasicParsing
```

### Expose local API to Android with ngrok

1. Start the backend locally and confirm `/health` responds.
2. Start ngrok forwarding the same local port, e.g. `ngrok http 5000`.
3. Copy the HTTPS forwarding URL from ngrok.
4. Set Android/GitHub Actions variable `DAYBOOK_API_BASE_URL` to that current HTTPS URL.
5. Recheck/update the variable whenever the ngrok URL changes (unless a reserved static domain is configured).
6. Do not commit tunnel credentials or private auth tokens.

A tunnel only forwards requests to the local laptop; it does not make the backend an always-on hosted service. The laptop, backend process, and ngrok process must all remain running.

## Processing flow and reliability notes

1. Android uploads audio using a short-lived presigned URL from `POST /api/recordings/upload-url`.
2. Backend creates a recording job in Neon PostgreSQL.
3. Local BackgroundService polls PostgreSQL and processes queued jobs.
4. FFmpeg/FFprobe prepare audio chunks (current profile uses 8-minute chunks).
5. Groq Whisper transcribes each chunk. Raw transcript is persisted as the recovery source.
6. Gemini optionally refines chunk transcripts.
7. Refined chunks are stitched; chunk summaries are used to generate the final MoM when enabled by the activity profile.
8. Job results are persisted in PostgreSQL and optionally mirrored to Firestore.

Important behavior:
- Raw STT transcript must remain available even if Gemini times out or fails.
- Gemini refinement and MoM are optional stages; a Gemini outage should not erase the raw transcript.
- Groq/Gemini free-tier quotas and provider availability are not guaranteed.
- A 503 from Gemini indicates temporary model/service unavailability; a successful model-list response is not a substitute for a direct `generateContent` test.
- Do not repeatedly retry while a provider quota is exhausted.

## Architecture and API references

- .NET 10 Minimal API + PostgreSQL polling BackgroundService (same local process).
- Neon PostgreSQL is the authoritative job/transcript database.
- Neon Object Storage stores original audio; backend issues short-lived presigned upload URLs.
- Firebase ID tokens authenticate Android; Firestore is an optional result mirror.
- Groq Whisper performs speech-to-text; Gemini refines transcripts and generates MoM.
- API endpoints: `GET /health`, `GET /api/activity-profiles`, `POST /api/recordings/upload-url`, `POST /api/recordings`, `GET /api/recordings/{id}`, `POST /api/recordings/{id}/retry`, and `POST /api/benchmarks/transcription`.

See [Windows setup](docs/FREE-SETUP-WINDOWS.md), [API contract](docs/API-CONTRACT.md), [architecture](docs/ARCHITECTURE.md), and [accuracy test plan](docs/ACCURACY-TEST-PLAN.md).

## Future migration checklist (local laptop → hosted environment)

When moving to another machine, VPS, container, or cloud platform:

- [ ] Provision a host that can run .NET 10 and keep the API/worker alive (service manager/container/orchestrator).
- [ ] Decide whether API and background worker stay in one process or are deployed separately. Ensure only intended worker instances process jobs concurrently.
- [ ] Provision PostgreSQL and migrate schema/data; verify connection pooling, TLS, backups, and restore procedure.
- [ ] Provision S3-compatible object storage or retain Neon Object Storage; migrate audio objects and update endpoint, region, bucket, access keys, and IAM permissions.
- [ ] Configure Firebase project/auth settings and securely provide the service-account identity (prefer workload identity/secret manager over a JSON file).
- [ ] Recreate Groq and Gemini credentials in the target secret manager; verify provider quotas, billing, data handling terms, and model availability.
- [ ] Set `ASPNETCORE_ENVIRONMENT`, `ASPNETCORE_URLS`, all required configuration keys, and FFmpeg/FFprobe paths for the target OS/image.
- [ ] Replace local/ngrok API URL with a stable HTTPS domain and update Android `DAYBOOK_API_BASE_URL`.
- [ ] Configure TLS termination, firewall/network rules, CORS if required, rate limits, authentication, logging, monitoring, and alerts.
- [ ] Configure persistent storage for temporary files if required; do not rely on ephemeral container disk for original recordings or durable job state.
- [ ] Test health check, upload, STT, Gemini refinement, MoM generation, retry behavior, and recovery after worker restart.
- [ ] Verify database backups and perform a restore test before switching Android production traffic.
- [ ] Rotate credentials that were used on the old machine or exposed during development.

## Provider/free-tier caveats

Neon, Firestore, Groq, and Gemini have finite quotas and changing product terms. The local API/worker stops when the laptop is off or asleep. Gemini free-tier data handling may permit submitted content to be used to improve Google products; do not submit confidential employer/customer recordings unless the applicable terms are acceptable. This is a development setup, not a production SLA.

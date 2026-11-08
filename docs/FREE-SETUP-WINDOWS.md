# Daybook Backend — Free setup on Windows 10 (no Docker)

This setup deliberately keeps the .NET API and BackgroundService on the Windows laptop. The laptop is the development host; it is not an always-on cloud server. Keep it powered on and connected while a recording job is being processed.

## Free-service design

| Function | Provider | Free allowance / constraint |
|---|---|---|
| PostgreSQL | Neon Free | 1 GB database per project, 100 CU-hours/month; compute scales to zero after inactivity |
| Audio files | Neon Object Storage | 5 GB per project; S3-compatible; choose a supported Singapore region (`ap-southeast-1`) |
| Login | Firebase Authentication | Use a no-cost sign-in provider such as Google |
| Result mirror | Cloud Firestore | Free quota: 1 GiB storage, 50,000 reads/day, 20,000 writes/day, 10 GiB outbound/month |
| Speech-to-text | Groq API Free | Whisper Large V3 / Large V3 Turbo; rate-limited; 25 MB audio upload limit on free tier |
| Transcript refinement / MoM | Gemini Developer API Free | Limited model and request quotas; free-tier content may be used to improve Google products |
| Backend | Local Windows 10 | No hosting fee; stops when laptop is off |

**Do not use Firebase Cloud Storage in this all-free/no-billing setup.** Firebase now requires the Blaze billing plan to access Cloud Storage buckets, even if usage stays within a no-cost allowance. This V1 uses Neon Object Storage instead. Android upload code must therefore change from Firebase Storage SDK upload to the backend-issued Neon S3 presigned PUT URL.

Free quotas and product terms can change. Check each provider dashboard before putting important recordings into the system. Never assume a free tier means unlimited use.

## 1. Create Neon project and database

1. Create a Neon account and a Free project.
2. Choose AWS Asia Pacific (Singapore), `ap-southeast-1`, so Neon Object Storage is available near Indonesia.
3. Copy the PostgreSQL connection string from the Neon dashboard. Keep the password private.
4. Open the SQL Editor in Neon and run `scripts-create-schema.sql`.
5. In the project's Storage section, create a **private** bucket named `daybook-audio`.
6. Create a Neon credential with `storage:read` and `storage:write` scopes. Copy the S3 Access Key ID (`token_id`) and S3 Secret Access Key immediately.
7. Copy the branch S3 endpoint and region. The endpoint looks like `https://<branch>.storage.<cell>.ap-southeast-1.aws.neon.tech`.

Neon Object Storage requires path-style S3 requests; this project configures that in the AWS SDK.

## 2. Firebase (no-cost products only)

1. Keep Firebase project ID `daybook-53705` if that is the project used by the Android app.
2. Enable Google sign-in in Firebase Authentication.
3. Create/enable the default Cloud Firestore database.
4. Do **not** enable Firebase Storage for this architecture.
5. Create a service account credential for the local backend. Grant the minimum Firestore permissions needed to write the result mirror. Download the JSON to a location outside the repository.
6. Set `GOOGLE_APPLICATION_CREDENTIALS` to that JSON file path. Never commit it.

Firestore free quota is finite. If the quota is reached on Spark, affected service usage is stopped until the quota resets or the plan changes.

## 3. Groq STT API (free tier)

1. Create a Groq account and API key.
2. Set `Stt__ApiKey` as a secret (instructions below).
3. This project calls `https://api.groq.com/openai/v1/audio/transcriptions` with `whisper-large-v3` and fallback `whisper-large-v3-turbo`.
4. Audio is split into 8-minute WAV chunks. A 16 kHz mono PCM WAV chunk of this length is approximately 15.4 MB, below the free-tier 25 MB request limit in normal conditions. The backend also rejects chunks above its configured 24 MB safety limit.
5. Groq free tier is rate-limited. Long recordings may need to continue after the quota resets. For 12-hour recordings, do not promise same-day completion. Completed chunks are checkpointed in PostgreSQL; retrying a job skips already completed chunks.

If Groq returns HTTP 429, wait for the quota reset and use the recording retry endpoint. Do not repeatedly retry while the quota remains exhausted.

## 4. Gemini API (free tier)

1. Create an API key in Google AI Studio.
2. Use the free-tier Gemini API key as `Gemini__ApiKey`.
3. Free-tier quotas are limited and can change. The free tier may use submitted content to improve Google products. Do not send confidential employer/customer meeting recordings or sensitive personal content through the free tier unless you have checked and accepted the provider's data terms.
4. Gemini refinement can be disabled per activity by setting `EnableGeminiRefinement` to `false`; MoM generation depends on the refinement pipeline.

## 5. Install Windows dependencies

1. Install **.NET 10 SDK**, not just the runtime: https://dotnet.microsoft.com/download/dotnet/10.0
2. Install FFmpeg and ensure both `ffmpeg.exe` and `ffprobe.exe` are in PATH.
3. Open a new PowerShell window and check:

```powershell
dotnet --version
ffmpeg -version
ffprobe -version
```

No Docker, local PostgreSQL, Python, or local Whisper model is required for this cloud-STT configuration.

## 6. Configure secrets with .NET User Secrets

Open PowerShell in the repository root and initialize secrets:

```powershell
dotnet user-secrets init --project .\src\Daybook.Backend\Daybook.Backend.csproj
```

Set the Neon PostgreSQL connection string (paste your actual connection string):

```powershell
dotnet user-secrets set "ConnectionStrings:Postgres" "Host=YOUR_HOST;Database=YOUR_DB;Username=YOUR_USER;Password=YOUR_PASSWORD;SSL Mode=Require" --project .\src\Daybook.Backend\Daybook.Backend.csproj
```

Set Neon Object Storage credentials:

```powershell
dotnet user-secrets set "Storage:Endpoint" "https://YOUR_BRANCH.storage.YOUR_CELL.ap-southeast-1.aws.neon.tech" --project .\src\Daybook.Backend\Daybook.Backend.csproj
dotnet user-secrets set "Storage:Region" "ap-southeast-1" --project .\src\Daybook.Backend\Daybook.Backend.csproj
dotnet user-secrets set "Storage:Bucket" "daybook-audio" --project .\src\Daybook.Backend\Daybook.Backend.csproj
dotnet user-secrets set "Storage:AccessKey" "YOUR_NEON_TOKEN_ID" --project .\src\Daybook.Backend\Daybook.Backend.csproj
dotnet user-secrets set "Storage:SecretKey" "YOUR_NEON_S3_SECRET" --project .\src\Daybook.Backend\Daybook.Backend.csproj
```

Set Groq and Gemini keys:

```powershell
dotnet user-secrets set "Stt:ApiKey" "YOUR_GROQ_API_KEY" --project .\src\Daybook.Backend\Daybook.Backend.csproj
dotnet user-secrets set "Gemini:ApiKey" "YOUR_GEMINI_API_KEY" --project .\src\Daybook.Backend\Daybook.Backend.csproj
```

Set the Firebase service-account path in the current PowerShell session (adjust the path):

```powershell
$env:GOOGLE_APPLICATION_CREDENTIALS = "C:\Secure\daybook-firebase-service-account.json"
```

For future sessions, set this environment variable in Windows Environment Variables, or add it to a local untracked `.env` loader of your choice. Do not put the JSON credential inside the repository.

## 7. Build and run without Docker

```powershell
dotnet restore .\DaybookBackend.sln
dotnet build .\DaybookBackend.sln
```

Then run:

```powershell
.\run-local.ps1
```

Or manually:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project .\src\Daybook.Backend\Daybook.Backend.csproj --urls "http://0.0.0.0:8080"
```

Check `http://localhost:8080/health`.

To call the API from an Android phone during local development, connect the phone and laptop to the same Wi-Fi, find the laptop's LAN IPv4 address using `ipconfig`, and use `http://LAPTOP_LAN_IP:8080` as the API base URL. Allow the app through Windows Firewall on a private network. The laptop must stay on while the worker processes jobs. For Android cleartext HTTP, use a debug-only network security configuration; use HTTPS before public deployment.

## 8. Android upload flow change required

The old Firebase Storage upload flow is no longer used. New flow:

1. Android gets Firebase ID token.
2. Android calls `POST /api/recordings/upload-url` with Bearer token and a client-generated GUID:

```json
{
  "clientRecordingId": "8d8a0bd0-08b4-4f84-9e8f-2c2f1d1a2c44",
  "contentType": "audio/wav"
}
```

3. API returns `uploadUrl`, `audioObjectName`, `expiresAt`, and `contentType`.
4. Android performs HTTP `PUT` to `uploadUrl`, with `Content-Type` exactly matching the returned content type, and streams the audio file as the request body.
5. Only after PUT succeeds, Android calls `POST /api/recordings` with the returned `audioObjectName`, `activityCode`, `durationSeconds`, and optional language override.
6. Store the returned recording ID and poll `GET /api/recordings/{id}` or listen to the Firestore mirror.

Keep a durable local copy until upload success. Do not delete source audio on upload or processing failure.

## 9. Postman testing

Import:
- `postman/Daybook-Backend-V1.postman_collection.json`
- `postman/Daybook-Backend-V1.postman_environment.json`

Set `baseUrl` to `http://localhost:8080`, `firebaseIdToken` to a current Firebase ID token, and other variables as needed. Run the new **Create Audio Upload URL** request, then upload the binary audio using the returned URL and matching Content-Type header. Then create the recording job.

## Free-tier boundaries to understand

- Neon Free has finite compute-hours, database storage and object-storage capacity. Idle database compute scales to zero; the first request after idle can be slower.
- Neon Object Storage currently has 5 GB free per project. Delete old recordings only after the retention policy is implemented and the user explicitly agrees; the current V1 does not auto-delete originals.
- Firestore has finite daily free quotas.
- Groq STT free tier has request/audio rate limits and 25 MB free upload limit. A 12-hour recording can take more than one day if the free audio quota is exhausted.
- Gemini free-tier quota is limited; free-tier data handling differs from paid tier. Do not use it for confidential content without reviewing terms.
- The .NET API/worker runs locally. It is not available when the laptop is off or asleep. There is no always-on free .NET hosting included in this plan.
- This is a zero-subscription-cost development setup, not an unlimited or production SLA setup. Free quotas, account eligibility and provider terms may change.

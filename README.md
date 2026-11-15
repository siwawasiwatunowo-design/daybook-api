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


## Daybook product overview — how the app works

This backend is one part of the Daybook Android application. Daybook has five main tabs:

| Tab | Purpose | Current data location |
|---|---|---|
| **Timebox** | Plan a day using time blocks and mark them done | Firebase Cloud Firestore |
| **Notes** | Personal notes, journal, learning notes, thoughts and ideas; rich-text editor and auto-save | Firebase Cloud Firestore |
| **Dreams** | Record dreams with date, type and sleep-quality rating | Firebase Cloud Firestore |
| **Bucket list** | Track life goals by category and status (Dream / Planned / Done) | Firebase Cloud Firestore |
| **Rekam (Recordings)** | Record meetings, mark important/confusing moments, process audio into a timed transcript and meeting summary | Audio and recording metadata/transcript are stored locally on Android; processing is delegated to this backend |

Authentication uses Firebase Authentication (email/password). The signed-in Firebase UID scopes Firestore entries. The recording API separately validates the Firebase ID token before accepting upload, job-creation and status requests.

### High-level app and backend flow

```mermaid
flowchart TD
  USER[User opens Daybook Android] --> AUTH[Firebase Authentication]
  AUTH --> HOME[Home: Timebox / Notes / Dreams / Bucket list / Rekam]
  HOME --> CRUD[Timebox, Notes, Dreams, Bucket list]
  CRUD <--> FS[(Cloud Firestore users/{uid}/entries/{entryId})]
  HOME --> REC[Rekam tab]
  REC --> MIC[Foreground RecordingService captures microphone]
  MIC --> LOCAL[(Android private app storage: audio chunks + index.json + marks)]
  LOCAL --> STOP[Stop recording]
  STOP --> WM[Android WorkManager: SttWorker]
  WM --> TOKEN[Get Firebase ID token]
  TOKEN --> API[Local .NET 10 Daybook API]
  API --> SIGN[Validate Firebase token]
  SIGN --> URL[POST /api/recordings/upload-url]
  URL --> S3URL[Return short-lived presigned URL]
  S3URL --> PUT[Android PUT audio bytes directly]
  PUT --> OBJ[(Neon S3-compatible Object Storage)]
  WM --> CREATE[POST /api/recordings]
  CREATE --> DB[(Neon PostgreSQL: job + chunk records)]
  CREATE --> WORKER[Local BackgroundService polls queued jobs]
  WORKER --> OBJ
  WORKER --> CHUNK[Prepare audio and split into 8-minute chunks]
  CHUNK --> GROQ[Groq Whisper STT]
  GROQ --> RAW[Persist raw transcript + checkpoints]
  RAW --> GEM[Gemini chunk refinement + summaries]
  GEM --> STITCH[Stitch refined transcript]
  STITCH --> MOM[Generate final MoM from chunk summaries]
  MOM --> SAVE[Persist transcript + MoM + job status]
  SAVE --> STATUS[GET /api/recordings/{id}]
  STATUS --> ANDROID[SttWorker polls and saves results locally]
  ANDROID --> RESULT[Result screen: MoM + timed transcript + audio playback]
  RESULT --> CHOICE{User chooses}
  CHOICE -->|Copy / Share| SHARE[Share text]
  CHOICE -->|Save to Notes| NOTE[Explicitly create a Notes entry]
  NOTE --> FS
```

### Recording journey, step by step

1. **Start recording.** In the Rekam tab, the Android foreground service captures microphone audio. The recording is stored under the app's private files directory, not the cache directory. The UI shows elapsed time and an audio-level indicator, with Bingung and Penting mark actions. Stop requires confirmation.
2. **Local recording data.** Android RecordingRepository maintains recording metadata, chunk metadata, marks, transcript segments and processing state in an atomically written index.json. Audio chunk files remain in the app's private recording directory. The recording is not automatically deleted after processing.
3. **Queue processing.** After recording is finalized, Android WorkManager runs one unique SttWorker for that recording. It prepares WAV audio from the local chunks. A signed-in Firebase user and internet access are required for cloud transcription.
4. **Request upload authorization.** Android sends its Firebase ID token to POST /api/recordings/upload-url, with a client recording ID and content type. The API returns a short-lived presigned URL and object name.
5. **Upload audio.** Android uploads WAV bytes directly to the presigned Neon Object Storage URL using HTTP PUT. The audio payload does not pass through the .NET API server.
6. **Create processing job.** Android calls POST /api/recordings with the object name, duration, activity code (currently GENERAL_MEETING) and language override (currently id). The API creates a queued job in PostgreSQL and returns its job ID.
7. **Server-side processing.** The local .NET BackgroundService polls PostgreSQL, retrieves the audio object, prepares 8-minute chunks, and calls Groq Whisper for speech-to-text. Each chunk is checkpointed so completed work can be retained across retries.
8. **AI refinement and MoM.** The backend persists raw transcript first. Gemini optionally refines each chunk and produces chunk summaries; the backend stitches refined text and generates the final MoM from chunk summaries. If Gemini times out or fails, raw STT text remains available and the job can finish in a review-needed state rather than losing the transcript.
9. **Return results to the phone.** Android polls GET /api/recordings/{id} approximately every three seconds (up to the worker's configured polling window). On Completed or NeedsReview, the app stores returned transcript segments and MoM in its local recording repository.
10. **Review and reuse.** The result screen displays MoM (when available) and timed transcript. Tapping a transcript segment seeks the local audio player to that moment. The user can copy/share text or explicitly choose Simpan ke Notes. Only the text is copied into a new Firestore Notes entry; the original recording remains on the phone.

### Data ownership and privacy boundaries

| Data | Primary location | Cloud behavior |
|---|---|---|
| Timebox / Notes / Dreams / Bucket list entries | Firestore under signed-in user's UID | Synced through Firebase |
| Original meeting audio | Android private app storage | Uploaded as a processing copy to Neon Object Storage when cloud transcription is requested |
| Processing audio object | Neon Object Storage bucket daybook-audio | Backend reads it for processing |
| Raw/refined transcript, chunk summaries, MoM, job status | Neon PostgreSQL | Stored by backend |
| Returned transcript and MoM | Android private app storage / RecordingRepository | Retrieved from backend; not automatically written to Firestore |
| Notes created with Simpan ke Notes | Firestore | Saved only after explicit user action |
| Firebase ID token | Android Authorization header to API | Used to authenticate API requests; must not be logged or persisted |

**Important distinction:** ordinary Notes/Timebox/Dreams/Bucket list data syncs through Firestore. Recording history, audio, transcript and MoM are kept in the Android local recording repository unless the user explicitly saves the text as a Note. The backend also stores the processing job, transcript and MoM in Neon PostgreSQL. The current result screen says audio remains on the phone after processing; an upload copy is stored in Neon Object Storage for cloud processing.

### Current deployment boundary

The Android app and Firebase/Neon/Groq/Gemini services are not all running on the laptop. The .NET API and its BackgroundService worker are currently hosted by the developer's Windows laptop. Neon PostgreSQL and Neon Object Storage, Firebase, Groq and Gemini are external cloud services. Android reaches the laptop-hosted API through ngrok during development. Turning off or sleeping the laptop stops job processing, even though the uploaded object and queued job remain in their cloud services.

### Source references

- Android app repository: https://github.com/siwawasiwatunowo-design/daybook
- Android API client: app/src/main/java/com/example/daybook/DaybookApiClient.kt
- Android cloud processing worker: app/src/main/java/com/example/daybook/SttWorker.kt
- Android recording repository/service: RecordingRepository.kt and RecordingService.kt
- Android result UI: RecordingResultScreen.kt
- Backend architecture: docs/ARCHITECTURE.md

> **Documentation alignment note:** the Android repository's SYSTEM_FLOW.md contains earlier design/phase notes, including proposed local Vosk and local-only recording flows. The currently integrated cloud transcription path is described here from the active DaybookApiClient.kt and SttWorker.kt implementation. Treat older phase/status tables as historical unless revalidated against current code.

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

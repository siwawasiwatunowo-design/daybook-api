# Postman — Daybook Backend Free-tier V1

Import the collection and environment. Set `baseUrl` (normally `http://localhost:8080`) and a current Firebase ID token.

## Request sequence

1. `Health` — check API.
2. `Create Audio Upload URL (Neon Object Storage)` — supply a GUID in `clientRecordingId`; response saves `uploadUrl`, `audioObjectName`, and `uploadContentType` into environment variables.
3. Upload the actual audio bytes with a separate Postman PUT request to `{{uploadUrl}}`, Body > binary, select audio file, and header `Content-Type: {{uploadContentType}}`. The presigned URL expires in 15 minutes.
4. `Create Recording Job` — use saved `audioObjectName`.
5. `Get Recording` — use the ID captured from create response.
6. Retry only after fixing the cause; if Groq returns HTTP 429, wait until free quota resets before retrying.

The backend does not accept multipart audio directly. Android must follow the same presigned-URL flow and must stop uploading to Firebase Storage.

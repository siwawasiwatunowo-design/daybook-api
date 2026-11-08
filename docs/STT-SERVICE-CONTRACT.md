# STT Provider Contract — Groq Free API

The .NET backend calls Groq's OpenAI-compatible endpoint directly; no local Python STT server is required.

- Endpoint: `https://api.groq.com/openai/v1/audio/transcriptions`
- Models: `whisper-large-v3`, `whisper-large-v3-turbo`
- Response format: `verbose_json` with segment timestamps
- Free-tier maximum file size: 25 MB. The backend uses a 24 MB safety guard.
- The backend creates 8-minute mono 16 kHz PCM WAV chunks, normally about 15.4 MB each.
- `VocabularyInstructions`, `VocabularyTerms`, and `VocabularyAliases` are sent as Whisper prompt context. This guides spelling but does not guarantee exact acronym recognition.
- Groq free usage is rate-limited. HTTP 429 means wait for quota reset, then retry. Completed chunks are checkpointed in PostgreSQL.
- Segment log probability / no-speech / compression diagnostics are only heuristics, not calibrated confidence or WER.

No local Python/Whisper service is started in this free-cloud configuration.

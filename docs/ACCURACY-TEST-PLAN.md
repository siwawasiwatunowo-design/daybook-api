# Daybook STT Accuracy Test Plan

## Purpose

Select STT models and activity-specific settings using measured performance on Daybook audio, not intuition or the diagnostic quality score.

## Test corpus

- Start with 10–20 representative clips per important System Activity.
- Include clean speech, distant speaker, quiet speaker, background noise, technical terminology, acronyms, numbers/dates and multiple speakers.
- Use short clips for fast model iteration; include at least one longer recording to validate chunk boundaries and recovery.
- Have a human create and verify a verbatim reference transcript for each exact audio file. Decide consistently whether fillers, false starts and non-speech sounds are included.
- Do not build the vocabulary dictionary from unverified/garbled STT output. Only add terms confirmed by the user or a trusted domain glossary.

## Controlled comparison

For each audio clip, keep the same activity profile, language, preprocessing, VAD and chunk/overlap settings while changing only the model. Then run a separate experiment for preprocessing/decoder settings. This isolates which change caused an improvement.

Use `POST /api/benchmarks/transcription` with the same audio object and reference transcript for 1–4 models per request. The endpoint uses the selected activity's preprocessing and chunk strategy and returns:

- WER, including substitution/deletion/insertion counts.
- CER (character error rate).
- Per-chunk diagnostic signals from the STT engine.
- The full hypothesis for manual review.

## Acceptance and analysis

- Initial engineering target: WER ≤15% on clean, clearly recorded Indonesian speech. This is a target, not a promise.
- Track technical-term/acronym accuracy and number/date accuracy separately; aggregate WER can hide high-impact domain errors.
- Compare quality across activity types and recording conditions, not only one sample.
- Record model name, model version, compute type, profile version, preprocessing, decoder and VAD settings with each benchmark run.
- Select the model based on accuracy, latency, memory/CPU/GPU cost and stability together.
- Keep the raw reference and benchmark result immutable so later model upgrades can be compared fairly.

## Limits

WER/CER are only as reliable as the reference transcript and normalization policy. The built-in heuristic QC is not an accuracy score. A high diagnostic score does not prove a correct transcript, and a low score does not always mean the words are wrong. Human review remains necessary for critical names, numbers, legal/financial decisions and unclear audio.

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Daybook.Backend.Domain;
using Daybook.Backend.Infrastructure;
using Daybook.Backend.Options;
using Microsoft.Extensions.Options;

namespace Daybook.Backend.Application;

public sealed class RecordingPipeline(
    IJobRepository jobs,
    IActivityProfileProvider profiles,
    IRecordingObjectStore objectStore,
    IAudioChunker chunker,
    ISpeechToTextProvider stt,
    ITranscriptQualityChecker qualityChecker,
    ITranscriptRefiner refiner,
    ISpeakingCoachEvaluator speakingCoach,
    IResultPublisher publisher,
    IOptions<WorkerOptions> workerOptions,
    IOptions<SttOptions> sttOptions,
    DaybookDbContext db,
    ILogger<RecordingPipeline> logger) : IRecordingPipeline
{
    public async Task ProcessAsync(Guid recordingId, CancellationToken ct)
    {
        var job = await jobs.GetAsync(recordingId, ct)
            ?? throw new InvalidOperationException($"Recording job {recordingId} was not found.");
        job.ProcessingStage = "PreparingAudio";
        job.ProcessingStep = 2;
        job.ProcessingTotalSteps = 7;
        job.ProcessingDetail = "Preparing audio for transcription";
        await jobs.UpdateJobAsync(job, ct);

        var profile = !string.IsNullOrWhiteSpace(job.ProfileSnapshotJson)
            ? JsonSerializer.Deserialize<ActivityProcessingProfile>(job.ProfileSnapshotJson)
                ?? await profiles.GetAsync(job.ActivityCode, ct)
            : await profiles.GetAsync(job.ActivityCode, ct);

        // Stable per-job folder: a restarted worker can recreate the same chunk files and
        // continue from chunk statuses already persisted in PostgreSQL.
        await publisher.PublishAsync(job, 0, 0, [], ct);
        var jobDirectory = Path.GetFullPath(Path.Combine(workerOptions.Value.TempDirectory, job.Id.ToString("N")));
        Directory.CreateDirectory(jobDirectory);
        var sourcePath = Path.Combine(jobDirectory, "source-audio.bin");
        var chunkDirectory = Path.Combine(jobDirectory, "chunks");
        await objectStore.DownloadToFileAsync(job.AudioObjectName, sourcePath, ct);
        var chunkPaths = await chunker.CreateChunksAsync(sourcePath, chunkDirectory, profile, ct);
        await jobs.EnsureChunksAsync(job.Id, chunkPaths, ct);
        var chunks = await jobs.GetChunksAsync(job.Id, ct);
        job.ProcessingStage = "Transcription";
        job.ProcessingStep = 3;
        job.ProcessingDetail = "Transcribing audio chunks";
        await jobs.UpdateJobAsync(job, ct);
        await publisher.PublishAsync(job, chunks.Count, chunks.Count(x => x.Status == ChunkStatus.Completed), [], ct);
        var needsReview = false;
        foreach (var chunk in chunks.OrderBy(x => x.Sequence))
        {
            ct.ThrowIfCancellationRequested();
            if (chunk.Status == ChunkStatus.Completed && !string.IsNullOrWhiteSpace(chunk.RawTranscript))
            {
                if (profile.QualityCheckEnabled && !string.IsNullOrWhiteSpace(chunk.QualityMetricsJson))
                {
                    try { using var metrics = JsonDocument.Parse(chunk.QualityMetricsJson);
                        needsReview |= metrics.RootElement.TryGetProperty("IsSuspicious", out var flag) && flag.GetBoolean(); }
                    catch (JsonException) { needsReview = true; }
                }
                continue;
            }

            chunk.Status = ChunkStatus.Processing;
            chunk.AttemptCount++;
            chunk.ErrorMessage = null;
            await jobs.UpdateChunkAsync(chunk, ct);
            await publisher.PublishAsync(job, chunks.Count, chunks.Count(x => x.Status == ChunkStatus.Completed), [chunk], ct);
            try
            {
                var language = job.LanguageOverride ?? profile.Language;
                var primaryModel = profile.PrimarySttModel ?? sttOptions.Value.PrimaryModel;
                var fallbackModel = profile.FallbackSttModel ?? sttOptions.Value.FallbackModel;
                var attempts = new List<object>();
                var primary = await stt.TranscribeAsync(chunk.AudioFileName, profile,
                    primaryModel, language, ct);
                var selected = primary;
                var quality = profile.QualityCheckEnabled
                    ? qualityChecker.Check(primary, profile)
                    : new TranscriptQualityResult(false, [], 0, 0, 0, null, null, null, null, 100);
                attempts.Add(new { model = primary.Model, text = primary.Text, detectedLanguage = primary.DetectedLanguage, durationSeconds = primary.DurationSeconds, quality.DiagnosticScore, quality.IsSuspicious, quality.Reasons });

                var candidateModels = new[] { fallbackModel }.Concat(profile.AlternativeSttModels)
                    .Where(x => !string.IsNullOrWhiteSpace(x) &&
                        !string.Equals(x, primaryModel, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(Math.Max(0, profile.MaxRetryModels));
                if (quality.IsSuspicious && profile.RetryWithFallbackModel)
                {
                    foreach (var candidateModel in candidateModels)
                    {
                        logger.LogWarning("Chunk {ChunkId} for job {JobId} looks suspicious; retrying with model {Model}.",
                            chunk.Id, job.Id, candidateModel);
                        var candidate = await stt.TranscribeAsync(chunk.AudioFileName, profile,
                            candidateModel!, language, ct);
                        var candidateQuality = qualityChecker.Check(candidate, profile);
                        attempts.Add(new { model = candidate.Model, text = candidate.Text, detectedLanguage = candidate.DetectedLanguage, durationSeconds = candidate.DurationSeconds, candidateQuality.DiagnosticScore,
                            candidateQuality.IsSuspicious, candidateQuality.Reasons });
                        // This heuristic score selects the strongest signal profile; it is not measured WER.
                        // A clean candidate always outranks a suspicious one, including score ties.
                        if ((quality.IsSuspicious && !candidateQuality.IsSuspicious) ||
                            (quality.IsSuspicious == candidateQuality.IsSuspicious &&
                             candidateQuality.DiagnosticScore > quality.DiagnosticScore))
                        {
                            selected = candidate;
                            quality = candidateQuality;
                        }
                        if (!quality.IsSuspicious) break;
                    }
                }

                needsReview |= quality.IsSuspicious;
                chunk.SelectedSttModel = selected.Model;
                chunk.QualityMetricsJson = JsonSerializer.Serialize(quality);
                chunk.SttAttemptsJson = JsonSerializer.Serialize(attempts);
                chunk.SttSegmentsJson = JsonSerializer.Serialize(selected.Segments);
                chunk.RawTranscript = selected.Text;
                chunk.Status = ChunkStatus.Completed;
                await jobs.UpdateChunkAsync(chunk, ct);
                job.UpdatedAt = DateTimeOffset.UtcNow; // renew the processing lease after each durable checkpoint
                await jobs.UpdateJobAsync(job, ct);
                await publisher.PublishAsync(job, chunks.Count, chunks.Count(x => x.Status == ChunkStatus.Completed), [chunk], ct);
            }
            catch (Exception ex)
            {
                chunk.Status = ChunkStatus.Failed;
                chunk.ErrorMessage = ex.Message;
                await jobs.UpdateChunkAsync(chunk, ct);
                throw;
            }
        }

        chunks = await jobs.GetChunksAsync(job.Id, ct);
        var rawTranscript = TranscriptStitcher.Stitch(chunks.OrderBy(x => x.Sequence)
            .Select(x => x.RawTranscript));
        if (string.IsNullOrWhiteSpace(rawTranscript))
            throw new InvalidOperationException("STT returned an empty transcript for all chunks.");

        // Persist successful STT before optional AI processing so the raw transcript is available early.
        job.RawTranscript = rawTranscript;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await jobs.UpdateJobAsync(job, ct);
        await publisher.PublishAsync(job, chunks.Count, chunks.Count(x => x.Status == ChunkStatus.Completed), chunks, ct);

        job.ProcessingStage = "Refinement";
        job.ProcessingStep = 4;
        job.ProcessingDetail = "Refining transcript and preserving source text";
        await jobs.UpdateJobAsync(job, ct);

        // Give each optional Gemini operation its own bounded budget. A single slow
        // chunk must not consume the time reserved for every later chunk or final MoM.
        var aiTimedOut = false;
        const int refinementTimeoutSeconds = 90;

        // Refine each chunk independently. A long recording is never sent as one oversized prompt.
        // If one chunk times out, keep its raw text and continue with the remaining chunks.
        foreach (var chunk in chunks.OrderBy(x => x.Sequence))
        {
            if (string.IsNullOrWhiteSpace(chunk.RawTranscript)) continue;
            if (ShouldRunRefinement(chunk.RefinedTranscript, chunk.ChunkSummaryJson))
            {
                if (!profile.EnableGeminiRefinement)
                {
                    chunk.RefinedTranscript = chunk.RawTranscript;
                    chunk.ChunkSummaryJson = CreateRefinementSummary(chunk.RawTranscript, "disabled");
                }
                else
                {
                    using var refinementCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    refinementCts.CancelAfter(TimeSpan.FromSeconds(refinementTimeoutSeconds));
                    try
                    {
                        var refinedChunk = await refiner.RefineChunkAsync(chunk.RawTranscript, profile, refinementCts.Token);
                        chunk.RefinedTranscript = refinedChunk.RefinedTranscript;
                        chunk.ChunkSummaryJson = CreateRefinementSummary(refinedChunk.ChunkSummaryJson, "completed");
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (OperationCanceledException) when (refinementCts.IsCancellationRequested)
                    {
                        aiTimedOut = true;
                        needsReview = true;
                        logger.LogWarning("Gemini refinement timed out after {TimeoutSeconds}s for chunk {ChunkId} in recording {RecordingId}; keeping raw transcript and continuing.",
                            refinementTimeoutSeconds, chunk.Id, job.Id);
                        chunk.RefinedTranscript = chunk.RawTranscript;
                        chunk.ChunkSummaryJson = CreateRefinementSummary(chunk.RawTranscript, "timeout");
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex,
                            "Gemini refinement failed for chunk {ChunkId} in recording {RecordingId}; keeping raw transcript.",
                            chunk.Id, job.Id);
                        needsReview = true;
                        chunk.RefinedTranscript = chunk.RawTranscript;
                        chunk.ChunkSummaryJson = CreateRefinementSummary(chunk.RawTranscript, "failed", ex.Message);
                    }
                }
                await jobs.UpdateChunkAsync(chunk, ct);
            }
        }

        chunks = await jobs.GetChunksAsync(job.Id, ct);
        var refinedTranscript = TranscriptStitcher.Stitch(chunks.OrderBy(x => x.Sequence)
            .Select(x => x.RefinedTranscript ?? x.RawTranscript));
        var summaries = chunks.OrderBy(x => x.Sequence)
            .Select(x => GetSummaryForMom(x.ChunkSummaryJson))
            .Where(x => !string.IsNullOrWhiteSpace(x)).ToList();

        // Preserve raw STT output separately; refined text is an additional artifact.
        job.RawTranscript = rawTranscript;
        job.RefinedTranscript = refinedTranscript;
        await jobs.UpdateJobAsync(job, ct);
        await publisher.PublishAsync(job, chunks.Count, chunks.Count(x => x.Status == ChunkStatus.Completed), chunks, ct);

        job.ProcessingStage = "ContentAnalysis";
        job.ProcessingStep = 5;
        job.ProcessingDetail = "Preparing summary and context";
        await jobs.UpdateJobAsync(job, ct);

        job.MomJson = "";
        if (profile.GenerateMom)
        {
            using var momCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            momCts.CancelAfter(TimeSpan.FromSeconds(90));
            try
            {
                job.MomJson = await refiner.GenerateFinalMomAsync(summaries, profile, momCts.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (momCts.IsCancellationRequested)
            {
                needsReview = true;
                aiTimedOut = true;
                logger.LogWarning("Gemini MoM exceeded its independent 90-second budget for {RecordingId}; transcript remains available.", job.Id);
            }
            catch (Exception ex)
            {
                // MoM generation is also optional; do not lose a completed transcript.
                logger.LogWarning(ex,
                    "Gemini MoM generation failed for recording {RecordingId}; transcript remains available.",
                    job.Id);
                needsReview = true;
            }
        }

        // Speaking Coach is queued as a separate evaluation job. Recording/STT is complete
        // regardless of Gemini availability; the evaluation worker reuses this saved transcript.
        if (job.EnableSpeakingCoach)
        {
            var alreadyQueued = await db.SpeakingCoachEvaluations.AnyAsync(
                x => x.RecordingJobId == job.Id && x.Version == 1, ct);
            if (!alreadyQueued)
            {
                db.SpeakingCoachEvaluations.Add(new SpeakingCoachEvaluation
                {
                    Id = Guid.NewGuid(),
                    RecordingJobId = job.Id,
                    UserId = job.UserId,
                    Version = 1,
                    Status = "Queued"
                });
            }
            job.SpeakingCoachStatus = "Queued";
            job.SpeakingCoachError = null;
            job.ProcessingStage = "SpeakingEvaluation";
            job.ProcessingStep = 6;
            job.ProcessingDetail = "Waiting for Speaking Coach evaluation";
        }
        else
        {
            job.ProcessingStage = "Finalizing";
            job.ProcessingStep = 7;
            job.ProcessingDetail = "Recording processing completed";
        }

        job.Status = needsReview ? JobStatus.NeedsReview : JobStatus.Completed;
        job.ErrorMessage = needsReview
            ? aiTimedOut
                ? "Transcript is available. Optional Gemini processing timed out; raw transcript was kept and remaining AI steps were attempted independently."
                : "Transcript is available, but one or more quality checks or AI refinement/MoM steps need review."
            : null;
        job.CompletedAt = DateTimeOffset.UtcNow;
        await jobs.UpdateJobAsync(job, ct);
        await db.SaveChangesAsync(ct);
        await publisher.PublishAsync(job, chunks.Count, chunks.Count(x => x.Status == ChunkStatus.Completed), chunks, ct);

        // Temp audio is retained for now to support diagnosis/reprocessing. Add a retention
        // policy before production deployment rather than deleting source audio immediately.
        logger.LogInformation("Recording job {JobId} finished with status {Status}.", job.Id, job.Status);
    }
    private static bool ShouldRunRefinement(string? refinedTranscript, string? chunkSummaryJson)
    {
        if (string.IsNullOrWhiteSpace(refinedTranscript)) return true;
        if (string.IsNullOrWhiteSpace(chunkSummaryJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(chunkSummaryJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("refinementStatus", out var status))
            {
                var value = status.GetString();
                return !string.Equals(value, "completed", StringComparison.OrdinalIgnoreCase) &&
                       !string.Equals(value, "disabled", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (JsonException) { }
        // Legacy summary formats without status metadata are treated as completed.
        return false;
    }

    private static string CreateRefinementSummary(string summary, string status, string? error = null) =>
        JsonSerializer.Serialize(new { summary, refinementStatus = status, refinementError = error });

    private static string GetSummaryForMom(string? summaryJson)
    {
        if (string.IsNullOrWhiteSpace(summaryJson)) return "";
        try
        {
            using var document = JsonDocument.Parse(summaryJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("summary", out var summary))
                return summary.ValueKind == JsonValueKind.String ? summary.GetString() ?? "" : summary.GetRawText();
        }
        catch (JsonException) { }
        return summaryJson;
    }

}

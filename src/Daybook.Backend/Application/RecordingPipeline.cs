using System.Text.Json;
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
    IResultPublisher publisher,
    IOptions<WorkerOptions> workerOptions,
    IOptions<SttOptions> sttOptions,
    ILogger<RecordingPipeline> logger) : IRecordingPipeline
{
    public async Task ProcessAsync(Guid recordingId, CancellationToken ct)
    {
        var job = await jobs.GetAsync(recordingId, ct)
            ?? throw new InvalidOperationException($"Recording job {recordingId} was not found.");
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
        await publisher.PublishAsync(job, chunks.Count, chunks.Count(x => x.Status == ChunkStatus.Completed), [], ct);
        var needsReview = false;
        var geminiAvailable = profile.EnableGeminiRefinement;
        string? aiWarning = null;
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
                attempts.Add(new { model = primary.Model, quality.DiagnosticScore, quality.IsSuspicious, quality.Reasons });

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
                        attempts.Add(new { model = candidate.Model, candidateQuality.DiagnosticScore,
                            candidateQuality.IsSuspicious, candidateQuality.Reasons });
                        // This heuristic score selects the strongest signal profile; it is not measured WER.
                        if (candidateQuality.DiagnosticScore > quality.DiagnosticScore)
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

        // Refine each chunk independently. A 12-hour transcript is never sent to Gemini
        // as one oversized prompt; only compact segment summaries enter final MoM synthesis.
        foreach (var chunk in chunks.OrderBy(x => x.Sequence))
        {
            if (string.IsNullOrWhiteSpace(chunk.RawTranscript)) continue;
            if (string.IsNullOrWhiteSpace(chunk.RefinedTranscript))
            {
                if (!geminiAvailable)
                {
                    chunk.RefinedTranscript = chunk.RawTranscript;
                    chunk.ChunkSummaryJson = JsonSerializer.Serialize(new { summary = chunk.RawTranscript });
                }
                else
                {
                    try
                    {
                        var refinedChunk = await refiner.RefineChunkAsync(chunk.RawTranscript, profile, ct);
                        chunk.RefinedTranscript = string.IsNullOrWhiteSpace(refinedChunk.RefinedTranscript)
                            ? chunk.RawTranscript : refinedChunk.RefinedTranscript;
                        chunk.ChunkSummaryJson = string.IsNullOrWhiteSpace(refinedChunk.ChunkSummaryJson)
                            ? JsonSerializer.Serialize(new { summary = chunk.RawTranscript })
                            : refinedChunk.ChunkSummaryJson;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        // Gemini is an enhancement, not a dependency for preserving a valid STT result.
                        geminiAvailable = false;
                        needsReview = true;
                        aiWarning = $"Gemini refinement unavailable; raw transcript preserved. {ex.Message}";
                        logger.LogWarning(ex, "Gemini refinement failed for job {JobId}; falling back to raw STT text.", job.Id);
                        chunk.RefinedTranscript = chunk.RawTranscript;
                        chunk.ChunkSummaryJson = JsonSerializer.Serialize(new { summary = chunk.RawTranscript });
                    }
                }
                await jobs.UpdateChunkAsync(chunk, ct);
            }
        }

        chunks = await jobs.GetChunksAsync(job.Id, ct);
        var refinedTranscript = TranscriptStitcher.Stitch(chunks.OrderBy(x => x.Sequence)
            .Select(x => x.RefinedTranscript ?? x.RawTranscript));
        var summaries = chunks.OrderBy(x => x.Sequence)
            .Select(x => x.ChunkSummaryJson ?? "")
            .Where(x => !string.IsNullOrWhiteSpace(x)).ToList();

        // Preserve raw STT output separately; refined text is an additional artifact.
        job.RawTranscript = rawTranscript;
        job.RefinedTranscript = refinedTranscript;
        await jobs.UpdateJobAsync(job, ct);
        await publisher.PublishAsync(job, chunks.Count, chunks.Count(x => x.Status == ChunkStatus.Completed), chunks, ct);

        if (profile.GenerateMom && geminiAvailable)
        {
            try { job.MomJson = await refiner.GenerateFinalMomAsync(summaries, profile, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                needsReview = true;
                aiWarning = $"Gemini MoM generation unavailable; transcript is still available. {ex.Message}";
                job.MomJson = "";
                logger.LogWarning(ex, "Gemini MoM generation failed for job {JobId}; keeping transcript result.", job.Id);
            }
        }
        else job.MomJson = "";
        job.Status = needsReview ? JobStatus.NeedsReview : JobStatus.Completed;
        job.ErrorMessage = aiWarning ?? (needsReview
            ? "One or more chunks triggered transcript quality checks. Review the transcript before relying on it."
            : null);
        job.CompletedAt = DateTimeOffset.UtcNow;
        await jobs.UpdateJobAsync(job, ct);
        await publisher.PublishAsync(job, chunks.Count, chunks.Count(x => x.Status == ChunkStatus.Completed), chunks, ct);

        // Temp audio is retained for now to support diagnosis/reprocessing. Add a retention
        // policy before production deployment rather than deleting source audio immediately.
        logger.LogInformation("Recording job {JobId} finished with status {Status}.", job.Id, job.Status);
    }
}

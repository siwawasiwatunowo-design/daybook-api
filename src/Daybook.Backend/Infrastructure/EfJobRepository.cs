using System.Data;
using System.Text.Json;
using Daybook.Backend.Application;
using Daybook.Backend.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Daybook.Backend.Options;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Daybook.Backend.Infrastructure;

public sealed class EfJobRepository(DaybookDbContext db, IOptions<WorkerOptions> workerOptions) : IJobRepository
{
    public async Task<RecordingJob> CreateAsync(string userId, CreateRecordingRequest request,
        Daybook.Backend.Options.ActivityProcessingProfile profile, CancellationToken ct)
    {
        var job = new RecordingJob
        {
            Id = Guid.NewGuid(), UserId = userId, ActivityCode = request.ActivityCode.Trim(),
            AudioObjectName = request.AudioObjectName.Trim(), DurationSeconds = request.DurationSeconds,
            LanguageOverride = request.LanguageOverride, ProfileVersion = profile.Version,
            ProfileSnapshotJson = JsonSerializer.Serialize(profile), Status = JobStatus.Queued
        };
        db.RecordingJobs.Add(job);
        await db.SaveChangesAsync(ct);
        return job;
    }

    public async Task<RecordingJobView?> GetForUserAsync(Guid id, string userId, CancellationToken ct)
    {
        var job = await db.RecordingJobs.AsNoTracking().Include(x => x.Chunks)
            .SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        return job is null ? null : new RecordingJobView(job.Id, job.ActivityCode, job.Status,
            job.DurationSeconds, job.Chunks.Count, job.Chunks.Count(x => x.Status == ChunkStatus.Completed),
            job.ErrorMessage, job.RawTranscript, job.RefinedTranscript, job.MomJson,
            job.Chunks.OrderBy(x => x.Sequence).Select(x => new RecordingChunkView(x.Sequence, x.Status,
                x.AttemptCount, x.SelectedSttModel, x.QualityMetricsJson, x.SttAttemptsJson,
                x.SttSegmentsJson, x.ErrorMessage)).ToArray(),
            job.CreatedAt, job.UpdatedAt);
    }

    public async Task<bool> RequestRetryAsync(Guid id, string userId, CancellationToken ct)
    {
        var job = await db.RecordingJobs.Include(x => x.Chunks)
            .SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        if (job is null || job.Status is JobStatus.Processing or JobStatus.Queued or JobStatus.Completed) return false;
        var chunksToReset = job.Status == JobStatus.NeedsReview
            ? job.Chunks
            : job.Chunks.Where(x => x.Status != ChunkStatus.Completed);
        foreach (var chunk in chunksToReset)
        {
            chunk.Status = ChunkStatus.Pending;
            chunk.ErrorMessage = null;
            if (job.Status == JobStatus.NeedsReview)
            {
                chunk.RawTranscript = null;
                chunk.RefinedTranscript = null;
                chunk.ChunkSummaryJson = null;
                chunk.SelectedSttModel = null;
                chunk.QualityMetricsJson = null;
                chunk.SttAttemptsJson = null;
                chunk.SttSegmentsJson = null;
            }
            chunk.UpdatedAt = DateTimeOffset.UtcNow;
        }
        if (job.Status == JobStatus.NeedsReview)
        {
            job.RawTranscript = null;
            job.RefinedTranscript = null;
            job.MomJson = null;
            job.CompletedAt = null;
        }
        job.Status = JobStatus.Queued;
        job.ErrorMessage = null;
        job.AttemptCount = 0;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<Guid?> TryClaimNextAsync(CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await using (var recover = new NpgsqlCommand(
            "UPDATE \"RecordingJobs\" SET \"Status\" = CASE WHEN \"AttemptCount\" >= @maxAttempts THEN @failed ELSE @queued END, \"ErrorMessage\" = 'Recovered after worker lease expired', \"UpdatedAt\" = @now WHERE \"Status\" = @processing AND \"UpdatedAt\" < @lease;",
            connection, (NpgsqlTransaction)tx.GetDbTransaction()))
        {
            recover.Parameters.AddWithValue("queued", (int)JobStatus.Queued);
            recover.Parameters.AddWithValue("processing", (int)JobStatus.Processing);
            recover.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
            recover.Parameters.AddWithValue("lease", DateTimeOffset.UtcNow.AddHours(-1));
            recover.Parameters.AddWithValue("maxAttempts", Math.Max(1, workerOptions.Value.MaxJobAttempts));
            recover.Parameters.AddWithValue("failed", (int)JobStatus.Failed);
            await recover.ExecuteNonQueryAsync(ct);
        }
        await using var command = new NpgsqlCommand(
            "UPDATE \"RecordingJobs\" SET \"Status\" = @processing, \"AttemptCount\" = \"AttemptCount\" + 1, \"UpdatedAt\" = @now " +
            "WHERE \"Id\" = (SELECT \"Id\" FROM \"RecordingJobs\" WHERE \"Status\" = @queued ORDER BY \"CreatedAt\" LIMIT 1 FOR UPDATE SKIP LOCKED) RETURNING \"Id\";",
            connection, (NpgsqlTransaction)tx.GetDbTransaction());
        command.Parameters.AddWithValue("processing", (int)JobStatus.Processing);
        command.Parameters.AddWithValue("queued", (int)JobStatus.Queued);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        var result = await command.ExecuteScalarAsync(ct);
        await tx.CommitAsync(ct);
        return result is Guid id ? id : null;
    }

    public Task<RecordingJob?> GetAsync(Guid id, CancellationToken ct) =>
        db.RecordingJobs.Include(x => x.Chunks).SingleOrDefaultAsync(x => x.Id == id, ct);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    public async Task EnsureChunksAsync(Guid jobId, IReadOnlyList<string> chunkNames, CancellationToken ct)
    {
        var existing = await db.RecordingChunks.Where(x => x.RecordingJobId == jobId).AnyAsync(ct);
        if (existing) return;
        for (var i = 0; i < chunkNames.Count; i++)
            db.RecordingChunks.Add(new RecordingChunk { Id = Guid.NewGuid(), RecordingJobId = jobId,
                Sequence = i, AudioFileName = chunkNames[i], Status = ChunkStatus.Pending });
        await db.SaveChangesAsync(ct);
    }

    public Task<List<RecordingChunk>> GetChunksAsync(Guid jobId, CancellationToken ct) =>
        db.RecordingChunks.Where(x => x.RecordingJobId == jobId).OrderBy(x => x.Sequence).ToListAsync(ct);

    public async Task UpdateChunkAsync(RecordingChunk chunk, CancellationToken ct)
    {
        chunk.UpdatedAt = DateTimeOffset.UtcNow;
        db.RecordingChunks.Update(chunk);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateJobAsync(RecordingJob job, CancellationToken ct)
    {
        job.UpdatedAt = DateTimeOffset.UtcNow;
        db.RecordingJobs.Update(job);
        await db.SaveChangesAsync(ct);
    }
}

using Daybook.Backend.Domain;
using Microsoft.EntityFrameworkCore;

namespace Daybook.Backend.Infrastructure;

public sealed class DaybookDbContext(DbContextOptions<DaybookDbContext> options) : DbContext(options)
{
    public DbSet<RecordingJob> RecordingJobs => Set<RecordingJob>();
    public DbSet<RecordingChunk> RecordingChunks => Set<RecordingChunk>();
    public DbSet<SpeakingCoachEvaluation> SpeakingCoachEvaluations => Set<SpeakingCoachEvaluation>();
    public DbSet<TranscriptionBenchmark> TranscriptionBenchmarks => Set<TranscriptionBenchmark>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RecordingJob>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(128).IsRequired();
            e.Property(x => x.ActivityCode).HasMaxLength(80).IsRequired();
            e.Property(x => x.AudioObjectName).HasMaxLength(1024).IsRequired();
            e.Property(x => x.ProfileVersion).HasMaxLength(40).IsRequired();\n            e.Property(x => x.PracticeTitle).HasMaxLength(300);\n            e.Property(x => x.PracticeSessionId).HasMaxLength(128);\n            e.Property(x => x.PracticeMode).HasMaxLength(32);\n            e.Property(x => x.SourceNoteId).HasMaxLength(128);\n            e.HasIndex(x => new { x.UserId, x.EnableSpeakingCoach, x.CreatedAt });
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasMany(x => x.Chunks).WithOne().HasForeignKey(x => x.RecordingJobId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<SpeakingCoachEvaluation>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(128).IsRequired();
            e.Property(x => x.Status).HasMaxLength(32).IsRequired();
            e.HasIndex(x => new { x.RecordingJobId, x.Version }).IsUnique();
            e.HasIndex(x => new { x.Status, x.NextAttemptAt, x.CreatedAt });
            e.HasOne<RecordingJob>().WithMany().HasForeignKey(x => x.RecordingJobId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<TranscriptionBenchmark>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(128).IsRequired();
            e.Property(x => x.ActivityCode).HasMaxLength(80).IsRequired();
            e.Property(x => x.AudioObjectName).HasMaxLength(1024).IsRequired();
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
        });
        modelBuilder.Entity<RecordingChunk>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.RecordingJobId, x.Sequence }).IsUnique();
            e.Property(x => x.SelectedSttModel).HasMaxLength(100);
        });
    }
}

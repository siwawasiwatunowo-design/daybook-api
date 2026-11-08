using Daybook.Backend.Domain;
using Microsoft.EntityFrameworkCore;

namespace Daybook.Backend.Infrastructure;

public sealed class DaybookDbContext(DbContextOptions<DaybookDbContext> options) : DbContext(options)
{
    public DbSet<RecordingJob> RecordingJobs => Set<RecordingJob>();
    public DbSet<RecordingChunk> RecordingChunks => Set<RecordingChunk>();
    public DbSet<TranscriptionBenchmark> TranscriptionBenchmarks => Set<TranscriptionBenchmark>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RecordingJob>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(128).IsRequired();
            e.Property(x => x.ActivityCode).HasMaxLength(80).IsRequired();
            e.Property(x => x.AudioObjectName).HasMaxLength(1024).IsRequired();
            e.Property(x => x.ProfileVersion).HasMaxLength(40).IsRequired();
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasMany(x => x.Chunks).WithOne().HasForeignKey(x => x.RecordingJobId)
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

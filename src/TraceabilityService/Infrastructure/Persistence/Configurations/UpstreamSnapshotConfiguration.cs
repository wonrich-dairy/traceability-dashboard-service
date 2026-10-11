using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Api.Infrastructure.Persistence.Configurations;

public class UpstreamSnapshotConfiguration : IEntityTypeConfiguration<UpstreamSnapshot>
{
    public void Configure(EntityTypeBuilder<UpstreamSnapshot> builder)
    {
        builder.ToTable("upstream_snapshots");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.BatchId)
            .IsRequired()
            .HasMaxLength(50);

        // One snapshot per batch.
        builder.HasIndex(s => s.BatchId)
            .IsUnique();

        builder.Property(s => s.ResolutionStatus)
            .HasConversion<string>()
            .HasMaxLength(20);

        // The resolver picks up PendingRetry rows.
        builder.HasIndex(s => s.ResolutionStatus);

        builder.Property(s => s.Payload)
            .HasColumnType("json");

        builder.Property(s => s.LastAttemptAt)
            .HasColumnType("datetime(6)");

        builder.Property(s => s.ResolvedAt)
            .HasColumnType("datetime(6)");

        builder.Property(s => s.FailureReason)
            .HasMaxLength(500);

        builder.HasOne<BatchRecord>()
            .WithOne(b => b.UpstreamSnapshot)
            .HasForeignKey<UpstreamSnapshot>(s => s.BatchId)
            .HasPrincipalKey<BatchRecord>(b => b.BatchId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

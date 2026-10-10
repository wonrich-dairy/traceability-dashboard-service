using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Api.Infrastructure.Persistence.Configurations;

public class TimelineEventConfiguration : IEntityTypeConfiguration<TimelineEvent>
{
    public void Configure(EntityTypeBuilder<TimelineEvent> builder)
    {
        builder.ToTable("timeline_events");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.BatchId)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(e => e.Checkpoint)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(e => e.EventType)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(e => e.OccurredAt)
            .HasColumnType("datetime(6)");

        builder.Property(e => e.RecordedBy)
            .HasMaxLength(100);

        builder.Property(e => e.Payload)
            .IsRequired()
            .HasColumnType("json");

        builder.Property(e => e.ContractVersion)
            .IsRequired()
            .HasMaxLength(10);

        builder.HasIndex(e => e.SourceEventId)
            .IsUnique();

        // A trace reads a batch's events in the order they happened.
        builder.HasIndex(e => new { e.BatchId, e.OccurredAt });

        // Events across all batches in a time window. The index above cannot serve
        // that, because OccurredAt is not its leading column.
        builder.HasIndex(e => e.OccurredAt);

        builder.HasOne<BatchRecord>()
            .WithMany(b => b.TimelineEvents)
            .HasForeignKey(e => e.BatchId)
            .HasPrincipalKey(b => b.BatchId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

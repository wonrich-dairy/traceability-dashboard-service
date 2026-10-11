using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Api.Infrastructure.Persistence.Configurations;

public class DeviationRecordConfiguration : IEntityTypeConfiguration<DeviationRecord>
{
    public void Configure(EntityTypeBuilder<DeviationRecord> builder)
    {
        builder.ToTable("deviation_records");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.BatchId)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(d => d.Checkpoint)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(d => d.OccurredAt)
            .HasColumnType("datetime(6)");

        builder.Property(d => d.ReasonCode)
            .HasMaxLength(50);

        // Deviations of one kind over time, e.g. every TemperatureHigh this month.
        builder.HasIndex(d => new { d.ReasonCode, d.OccurredAt });

        builder.Property(d => d.Parameter)
            .HasMaxLength(50);

        // decimal(5,2): up to 999.99, enough for temperatures, pH and percentages,
        // and stored exactly rather than as the nearest binary fraction.
        builder.Property(d => d.ObservedValue)
            .HasPrecision(5, 2);

        builder.Property(d => d.Limit)
            .HasPrecision(5, 2);

        builder.Property(d => d.Description)
            .HasMaxLength(500);

        // A batch's deviations in the order they happened.
        builder.HasIndex(d => new { d.BatchId, d.OccurredAt });

        // Deviations over a date range, split by checkpoint.
        builder.HasIndex(d => new { d.OccurredAt, d.Checkpoint });

        builder.HasOne<BatchRecord>()
            .WithMany(b => b.Deviations)
            .HasForeignKey(d => d.BatchId)
            .HasPrincipalKey(b => b.BatchId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<TimelineEvent>()
            .WithMany()
            .HasForeignKey(d => d.TimelineEventId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

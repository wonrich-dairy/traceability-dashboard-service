using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Api.Infrastructure.Persistence.Configurations;

public class CheckpointRecordConfiguration : IEntityTypeConfiguration<CheckpointRecord>
{
    public void Configure(EntityTypeBuilder<CheckpointRecord> builder)
    {
        builder.ToTable("checkpoint_records");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.BatchId)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(r => r.CheckpointName)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(r => r.RecordedAtTimestamp)
            .HasColumnType("datetime(6)");

        builder.Property(r => r.FatPercentage).HasPrecision(5, 2);
        builder.Property(r => r.SnfPercentage).HasPrecision(5, 2);
        builder.Property(r => r.ClrReading).HasPrecision(5, 2);
        builder.Property(r => r.TemperatureCelsius).HasPrecision(5, 2);
        builder.Property(r => r.PH).HasPrecision(5, 2);

        builder.Property(r => r.Appearance).HasConversion<string>().HasMaxLength(10);
        builder.Property(r => r.Texture).HasConversion<string>().HasMaxLength(10);
        builder.Property(r => r.Taste).HasConversion<string>().HasMaxLength(10);
        builder.Property(r => r.Colour).HasConversion<string>().HasMaxLength(10);
        builder.Property(r => r.Smell).HasConversion<string>().HasMaxLength(10);

        // The dashboard reads a batch's checkpoints in recorded order.
        builder.HasIndex(r => new { r.BatchId, r.RecordedAtTimestamp });

        builder.HasOne<BatchTrace>()
            .WithMany(t => t.Checkpoints)
            .HasForeignKey(r => r.BatchId)
            .HasPrincipalKey(t => t.BatchId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

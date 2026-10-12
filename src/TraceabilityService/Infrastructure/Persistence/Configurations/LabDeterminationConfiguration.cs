using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Api.Infrastructure.Persistence.Configurations;

public class LabDeterminationConfiguration : IEntityTypeConfiguration<LabDetermination>
{
    public void Configure(EntityTypeBuilder<LabDetermination> builder)
    {
        builder.ToTable("lab_determinations");

        builder.HasKey(d => d.Id);

        // A redelivered BatchCleared or BatchFailed cannot add a second row.
        builder.HasIndex(d => d.DeterminationId)
            .IsUnique();

        builder.Property(d => d.BatchId)
            .IsRequired()
            .HasMaxLength(50);

        // A batch's determinations in the order they were made. Also serves the
        // foreign key, so EF adds no separate index on BatchId.
        builder.HasIndex(d => new { d.BatchId, d.DeterminedAt });

        builder.Property(d => d.Result)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(d => d.DeterminedAt)
            .HasColumnType("datetime(6)");

        builder.Property(d => d.DeterminedBy)
            .IsRequired()
            .HasMaxLength(100);

        // Finding the row a supersede replaces, to mark it.
        builder.HasIndex(d => d.SupersedesDeterminationId);

        builder.Property(d => d.SupersededAt)
            .HasColumnType("datetime(6)");

        builder.HasOne<BatchRecord>()
            .WithMany(b => b.LabDeterminations)
            .HasForeignKey(d => d.BatchId)
            .HasPrincipalKey(b => b.BatchId)
            .OnDelete(DeleteBehavior.Cascade);

        // One determination per determination event, so one row per timeline row.
        builder.HasIndex(d => d.TimelineEventId)
            .IsUnique();

        builder.HasOne<TimelineEvent>()
            .WithMany()
            .HasForeignKey(d => d.TimelineEventId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

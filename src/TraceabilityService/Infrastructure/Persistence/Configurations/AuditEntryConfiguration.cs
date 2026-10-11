using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Api.Infrastructure.Persistence.Configurations;

public class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable("audit_entries");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.OccurredAt)
            .HasColumnType("datetime(6)");

        builder.Property(a => a.Actor)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(a => a.Action)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(a => a.EntityType)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(a => a.EntityId)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(a => a.Details)
            .HasColumnType("json");

        builder.Property(a => a.CorrelationId)
            .HasMaxLength(64);

        // The history of one record, newest first.
        builder.HasIndex(a => new { a.EntityType, a.EntityId, a.OccurredAt });

        builder.HasIndex(a => a.OccurredAt);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Api.Infrastructure.Persistence.Configurations;

public class PendingDispatchEventConfiguration : IEntityTypeConfiguration<PendingDispatchEvent>
{
    public void Configure(EntityTypeBuilder<PendingDispatchEvent> builder)
    {
        builder.ToTable("pending_dispatch_events");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.DispatchNumber)
            .IsRequired()
            .HasMaxLength(50);

        // Everything waiting for one dispatch, when its batch is created.
        builder.HasIndex(e => e.DispatchNumber);

        builder.Property(e => e.EventType)
            .IsRequired()
            .HasMaxLength(100);

        builder.HasIndex(e => e.SourceEventId)
            .IsUnique();

        builder.Property(e => e.Topic)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(e => e.Payload)
            .IsRequired()
            .HasColumnType("json");

        builder.Property(e => e.ReceivedAt)
            .HasColumnType("datetime(6)");
    }
}

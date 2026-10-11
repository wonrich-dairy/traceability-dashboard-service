using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Api.Infrastructure.Persistence.Configurations;

public class ProcessedMessageConfiguration : IEntityTypeConfiguration<ProcessedMessage>
{
    public void Configure(EntityTypeBuilder<ProcessedMessage> builder)
    {
        builder.ToTable("processed_messages");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.ConsumerGroup)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(m => m.Topic)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(m => m.ProcessedAt)
            .HasColumnType("datetime(6)");

        // The deduplication check. Per consumer group, so two consumers of the same
        // topic each process the event once.
        builder.HasIndex(m => new { m.ConsumerGroup, m.SourceEventId })
            .IsUnique();

        // Lets old rows be cleared by age.
        builder.HasIndex(m => m.ProcessedAt);
    }
}

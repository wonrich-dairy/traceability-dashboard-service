using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Api.Infrastructure.Persistence.Configurations;

public class BatchSourceConfiguration : IEntityTypeConfiguration<BatchSource>
{
    public void Configure(EntityTypeBuilder<BatchSource> builder)
    {
        builder.ToTable("batch_sources");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.BatchId)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(s => s.ConsignmentReference)
            .IsRequired()
            .HasMaxLength(100);

        // The same consignment can only be recorded against a batch once.
        builder.HasIndex(s => new { s.BatchId, s.ConsignmentReference })
            .IsUnique();

        builder.HasOne<BatchTrace>()
            .WithMany(t => t.Sources)
            .HasForeignKey(s => s.BatchId)
            .HasPrincipalKey(t => t.BatchId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

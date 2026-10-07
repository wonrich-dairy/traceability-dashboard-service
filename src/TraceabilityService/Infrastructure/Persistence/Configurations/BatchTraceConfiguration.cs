using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Api.Infrastructure.Persistence.Configurations;

public class BatchTraceConfiguration : IEntityTypeConfiguration<BatchTrace>
{
    public void Configure(EntityTypeBuilder<BatchTrace> builder)
    {
        builder.ToTable("batch_traces");

        builder.HasKey(t => t.Id);

        // BatchId is the business key the other two tables point at, so it has to
        // be unique and the same column type everywhere it appears. The alternate
        // key is what lets them use BatchId as their foreign key instead of Id, and
        // it carries the unique constraint.
        builder.Property(t => t.BatchId)
            .IsRequired()
            .HasMaxLength(50);

        builder.HasAlternateKey(t => t.BatchId);

        builder.Property(t => t.ProductionDate)
            .HasColumnType("date");

        // Every dashboard summary filters on a ProductionDate window and then
        // splits by CurrentStatus, so the range column leads and the status rides
        // along: MySQL can answer the status counts from the index alone, without
        // going back to the rows. The recent-failures query reuses the same
        // prefix for its window before narrowing to Rejected.
        builder.HasIndex(t => new { t.ProductionDate, t.CurrentStatus });

        builder.Property(t => t.CurrentStatus)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(t => t.FinalLabOutcome)
            .HasConversion<string>()
            .HasMaxLength(10);

        builder.Property(t => t.CreatedAt)
            .HasColumnType("datetime(6)");

        builder.Property(t => t.UpdatedAt)
            .HasColumnType("datetime(6)");
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Api.Infrastructure.Persistence.Configurations;

public class BatchRecordConfiguration : IEntityTypeConfiguration<BatchRecord>
{
    public void Configure(EntityTypeBuilder<BatchRecord> builder)
    {
        builder.ToTable("batch_records");

        builder.HasKey(b => b.Id);

        // BatchId is the business key the child tables point at, so it has to be
        // unique and the same column type everywhere it appears. The alternate key
        // is what lets them use BatchId as their foreign key instead of Id, and it
        // carries the unique constraint.
        builder.Property(b => b.BatchId)
            .IsRequired()
            .HasMaxLength(50);

        builder.HasAlternateKey(b => b.BatchId);

        builder.Property(b => b.DispatchRef)
            .HasMaxLength(50);

        // A trace can start from the dispatch number rather than the batch code.
        builder.HasIndex(b => b.DispatchRef);

        builder.Property(b => b.ProductLine)
            .IsRequired()
            .HasMaxLength(10);

        // Only six product lines, so on its own this index narrows little; with
        // Status after it, "failed FM batches" is answered from the index.
        builder.HasIndex(b => new { b.ProductLine, b.Status });

        builder.Property(b => b.Facility)
            .IsRequired()
            .HasMaxLength(50);

        // Every dashboard query filters on the caller's facility first, then
        // narrows to a status and shows the newest first.
        builder.HasIndex(b => new { b.Facility, b.Status, b.LastEventAt });

        builder.Property(b => b.Status)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(b => b.Completeness)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(b => b.FirstEventAt)
            .HasColumnType("datetime(6)");

        builder.Property(b => b.LastEventAt)
            .HasColumnType("datetime(6)");

        // Recent failures and the on-hold list filter on Status and show the
        // newest first.
        builder.HasIndex(b => new { b.Status, b.LastEventAt });
    }
}

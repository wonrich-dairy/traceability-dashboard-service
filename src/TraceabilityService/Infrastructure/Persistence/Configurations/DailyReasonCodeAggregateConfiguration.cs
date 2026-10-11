using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Api.Infrastructure.Persistence.Configurations;

public class DailyReasonCodeAggregateConfiguration : IEntityTypeConfiguration<DailyReasonCodeAggregate>
{
    public void Configure(EntityTypeBuilder<DailyReasonCodeAggregate> builder)
    {
        builder.ToTable("daily_reason_code_aggregates");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Date)
            .HasColumnType("date");

        builder.Property(a => a.ProductLine)
            .IsRequired()
            .HasMaxLength(10);

        builder.Property(a => a.Facility)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(a => a.ReasonCode)
            .IsRequired()
            .HasMaxLength(50);

        // One row per facility, day, product line and reason. Same leading columns
        // as daily_quality_aggregates, for the same date-window queries.
        // Named explicitly: the generated name is over MySQL's 64-character limit
        // and would be truncated.
        builder.HasIndex(a => new { a.Facility, a.Date, a.ProductLine, a.ReasonCode })
            .IsUnique()
            .HasDatabaseName("IX_daily_reason_code_aggregates_Facility_Date_Line_Reason");

        builder.Property(a => a.UpdatedAt)
            .HasColumnType("datetime(6)");
    }
}

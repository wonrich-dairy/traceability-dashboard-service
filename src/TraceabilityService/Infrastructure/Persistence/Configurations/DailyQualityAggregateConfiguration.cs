using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Api.Infrastructure.Persistence.Configurations;

public class DailyQualityAggregateConfiguration : IEntityTypeConfiguration<DailyQualityAggregate>
{
    public void Configure(EntityTypeBuilder<DailyQualityAggregate> builder)
    {
        builder.ToTable("daily_quality_aggregates");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Date)
            .HasColumnType("date");

        builder.Property(a => a.ProductLine)
            .IsRequired()
            .HasMaxLength(10);

        builder.Property(a => a.Facility)
            .IsRequired()
            .HasMaxLength(50);

        // One row per facility, day and product line. Facility leads because every
        // dashboard query fixes it; the date window is then a range scan.
        builder.HasIndex(a => new { a.Facility, a.Date, a.ProductLine })
            .IsUnique();

        builder.Property(a => a.UpdatedAt)
            .HasColumnType("datetime(6)");
    }
}

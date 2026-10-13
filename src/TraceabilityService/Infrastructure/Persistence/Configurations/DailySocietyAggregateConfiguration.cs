using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Api.Infrastructure.Persistence.Configurations;

public sealed class DailySocietyAggregateConfiguration : IEntityTypeConfiguration<DailySocietyAggregate>
{
    public void Configure(EntityTypeBuilder<DailySocietyAggregate> builder)
    {
        builder.ToTable("daily_society_aggregates");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Date).HasColumnType("date");
        builder.Property(x => x.SocietyCode).IsRequired().HasMaxLength(50);
        builder.Property(x => x.SocietyName).HasMaxLength(200);
        builder.Property(x => x.FatPercentTotal).HasPrecision(12, 4);
        builder.Property(x => x.SnfTotal).HasPrecision(12, 4);
        builder.Property(x => x.UpdatedAt).HasColumnType("datetime(6)");
        builder.HasIndex(x => new { x.Date, x.SocietyCode }).IsUnique();
    }
}

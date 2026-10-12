using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Api.Infrastructure.Persistence;

/// <summary>
/// The service's database context. Entity configurations live in the Configurations folder,
/// which OnModelCreating picks up automatically.
/// </summary>
public class TraceabilityDbContext(DbContextOptions<TraceabilityDbContext> options) : DbContext(options)
{
    public DbSet<BatchRecord> BatchRecords => Set<BatchRecord>();
    public DbSet<TimelineEvent> TimelineEvents => Set<TimelineEvent>();
    public DbSet<UpstreamSnapshot> UpstreamSnapshots => Set<UpstreamSnapshot>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();
    public DbSet<DeviationRecord> DeviationRecords => Set<DeviationRecord>();
    public DbSet<LabDetermination> LabDeterminations => Set<LabDetermination>();
    public DbSet<DailyQualityAggregate> DailyQualityAggregates => Set<DailyQualityAggregate>();
    public DbSet<DailyReasonCodeAggregate> DailyReasonCodeAggregates => Set<DailyReasonCodeAggregate>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TraceabilityDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Every timestamp is stored as UTC, but MySQL's datetime has no time zone, so EF
        // reads it back as DateTimeKind.Unspecified and the API would write it without a
        // Z. Marking it UTC on the way out keeps it consistent with the timestamps parsed
        // from JSON payloads, which do carry one. Values are stored unchanged.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcOnReadConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<UtcOnReadConverter>();
    }

    private sealed class UtcOnReadConverter() : ValueConverter<DateTime, DateTime>(
        v => v,
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
}
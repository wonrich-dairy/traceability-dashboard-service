using Microsoft.EntityFrameworkCore;
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
    public DbSet<DailyQualityAggregate> DailyQualityAggregates => Set<DailyQualityAggregate>();
    public DbSet<DailyReasonCodeAggregate> DailyReasonCodeAggregates => Set<DailyReasonCodeAggregate>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TraceabilityDbContext).Assembly);
    }
}
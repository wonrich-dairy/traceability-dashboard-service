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
    public DbSet<PendingDispatchEvent> PendingDispatchEvents => Set<PendingDispatchEvent>();
    public DbSet<DailyQualityAggregate> DailyQualityAggregates => Set<DailyQualityAggregate>();
    public DbSet<DailyReasonCodeAggregate> DailyReasonCodeAggregates => Set<DailyReasonCodeAggregate>();
    public DbSet<DailySocietyAggregate> DailySocietyAggregates => Set<DailySocietyAggregate>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TraceabilityDbContext).Assembly);

        if (!ProjectionTableScope.IncludeAuditEntries)
        {
            modelBuilder.Ignore<AuditEntry>();
        }

        var prefix = ProjectionTableScope.TablePrefix;
        if (prefix.Length > 0)
        {
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var tableName = entityType.GetTableName();
                if (tableName is not null && entityType.ClrType != typeof(AuditEntry))
                {
                    entityType.SetTableName(prefix + tableName);
                }
            }
        }
    }

    // audit_entries is append-only (SCRUM-138): an entry is added and never changed or removed.
    // Refused here for anything going through the change tracker; the table's triggers refuse
    // the rest (ExecuteUpdate, ExecuteDelete, raw SQL), so neither layer relies on the other.
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RefuseAuditEntryChanges();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        RefuseAuditEntryChanges();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void RefuseAuditEntryChanges()
    {
        var changed = ChangeTracker.Entries<AuditEntry>()
            .FirstOrDefault(e => e.State is EntityState.Modified or EntityState.Deleted);
        if (changed is not null)
        {
            throw new AuditEntryImmutableException(changed.Entity.Id, changed.State);
        }
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

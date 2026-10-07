using Microsoft.EntityFrameworkCore;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Api.Infrastructure.Persistence;
public class TraceabilityDbContext(DbContextOptions<TraceabilityDbContext> options) : DbContext(options)
{
    public DbSet<BatchTrace> BatchTraces => Set<BatchTrace>();
    public DbSet<BatchSource> BatchSources => Set<BatchSource>();
    public DbSet<CheckpointRecord> CheckpointRecords => Set<CheckpointRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TraceabilityDbContext).Assembly);
    }
}
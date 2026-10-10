using Microsoft.EntityFrameworkCore;

namespace TraceabilityService.Api.Infrastructure.Persistence;

/// <summary>
/// The service's database context. The entities, their configurations and the migrations are the
/// developer's work: add DbSets here and keep configurations in a Configurations folder, which
/// OnModelCreating picks up automatically.
/// </summary>
public class TraceabilityDbContext(DbContextOptions<TraceabilityDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TraceabilityDbContext).Assembly);
    }
}

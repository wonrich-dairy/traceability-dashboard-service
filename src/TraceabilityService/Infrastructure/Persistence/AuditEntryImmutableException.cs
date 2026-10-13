using Microsoft.EntityFrameworkCore;

namespace TraceabilityService.Api.Infrastructure.Persistence;

/// <summary>
/// An audit entry was changed or removed. Entries are append-only (SCRUM-138), so the save is
/// refused before anything reaches the database.
/// </summary>
public sealed class AuditEntryImmutableException(long id, EntityState state)
    : InvalidOperationException($"Audit entry {id} cannot be {(state == EntityState.Deleted ? "deleted" : "changed")}: audit entries are append-only.");

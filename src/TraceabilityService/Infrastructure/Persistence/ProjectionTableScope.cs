using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace TraceabilityService.Api.Infrastructure.Persistence;

/// <summary>
/// Routes DbContexts created in the current async flow to a prefixed shadow projection.
/// Live requests have no prefix and continue to use the active table names.
/// </summary>
public static class ProjectionTableScope
{
    private sealed record ScopeState(string Prefix, bool IncludeAuditEntries);
    private static readonly AsyncLocal<ScopeState?> Current = new();

    internal static string TablePrefix => Current.Value?.Prefix ?? string.Empty;
    internal static bool IncludeAuditEntries => Current.Value?.IncludeAuditEntries ?? true;

    public static IDisposable Enter(string prefix, bool includeAuditEntries = true)
    {
        var previous = Current.Value;
        Current.Value = new ScopeState(prefix, includeAuditEntries);
        return new ScopeLease(previous);
    }

    private sealed class ScopeLease(ScopeState? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Current.Value = previous;
            _disposed = true;
        }
    }
}

/// <summary>EF must cache one model per table prefix; otherwise a scoped shadow context could use active tables.</summary>
public sealed class TraceabilityModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) => context is TraceabilityDbContext
        ? (context.GetType(), ProjectionTableScope.TablePrefix, ProjectionTableScope.IncludeAuditEntries, designTime)
        : (object)(context.GetType(), designTime);
}

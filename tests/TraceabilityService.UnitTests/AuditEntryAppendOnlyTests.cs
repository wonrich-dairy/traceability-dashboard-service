using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using TraceabilityService.Api.Controllers;
using TraceabilityService.Api.Infrastructure.Persistence;
using TraceabilityService.Application;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.UnitTests;

/// <summary>
/// audit_entries is append-only at the application level (SCRUM-138): the context refuses to save
/// an entry changed or removed, and no endpoint writes the audit trail. The database's triggers,
/// the other half, are tested in AuditTrailEndpointTests against MySQL.
/// </summary>
public class AuditEntryAppendOnlyTests
{
    [Fact]
    public async Task An_entry_can_be_added()
    {
        await using var db = NewContext();

        db.AuditEntries.Add(Entry());
        await db.SaveChangesAsync();

        Assert.Equal(1, await db.AuditEntries.CountAsync());
    }

    [Fact]
    public async Task Changing_a_saved_entry_is_refused_and_nothing_is_saved()
    {
        await using var db = NewContext();
        var entry = Entry();
        db.AuditEntries.Add(entry);
        await db.SaveChangesAsync();

        entry.Actor = "someone-else";

        await Assert.ThrowsAsync<AuditEntryImmutableException>(() => db.SaveChangesAsync());
        Assert.Throws<AuditEntryImmutableException>(() => db.SaveChanges());

        await using var fresh = NewContext();
        Assert.Equal("qco-1", (await fresh.AuditEntries.SingleAsync()).Actor);
    }

    [Fact]
    public async Task Removing_a_saved_entry_is_refused_and_nothing_is_removed()
    {
        await using var db = NewContext();
        var entry = Entry();
        db.AuditEntries.Add(entry);
        await db.SaveChangesAsync();

        db.AuditEntries.Remove(entry);

        var refused = await Assert.ThrowsAsync<AuditEntryImmutableException>(() => db.SaveChangesAsync());
        Assert.Contains("deleted", refused.Message);

        await using var fresh = NewContext();
        Assert.Equal(1, await fresh.AuditEntries.CountAsync());
    }

    [Fact]
    public async Task An_entry_changed_through_an_attached_copy_is_refused_too()
    {
        await using var db = NewContext();
        db.AuditEntries.Add(Entry());
        await db.SaveChangesAsync();
        var id = (await db.AuditEntries.SingleAsync()).Id;

        await using var other = NewContext();
        other.AuditEntries.Update(new AuditEntry
        {
            Id = id, Actor = "forged", Action = "BatchTraceOpened", EntityType = "Batch", EntityId = "262-FM-B",
        });

        await Assert.ThrowsAsync<AuditEntryImmutableException>(() => other.SaveChangesAsync());
    }

    [Fact]
    public async Task Other_tables_are_still_updated_as_before()
    {
        await using var db = NewContext();
        var batch = new BatchRecord
        {
            BatchId = "262-FM-B", ProductLine = "FM", Facility = "FACTORY-01",
            FirstEventAt = DateTime.UtcNow, LastEventAt = DateTime.UtcNow,
        };
        db.BatchRecords.Add(batch);
        db.AuditEntries.Add(Entry());
        await db.SaveChangesAsync();

        batch.Status = BatchStatus.Failed;
        await db.SaveChangesAsync();
    }

    // No edit or delete endpoint: the audit trail's controller only reads, and its service has
    // no method that changes or removes an entry.
    [Fact]
    public void The_audit_trail_has_no_endpoint_that_writes()
    {
        var actions = typeof(AuditTrailController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any())
            .ToList();

        Assert.NotEmpty(actions);
        Assert.All(actions.SelectMany(a => a.GetCustomAttributes<HttpMethodAttribute>()),
            attribute => Assert.Equal(["GET"], attribute.HttpMethods));
    }

    [Fact]
    public void No_endpoint_anywhere_writes_to_the_audit_trail_route()
    {
        var writing = typeof(AuditTrailController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t))
            .Where(t => t.GetCustomAttribute<RouteAttribute>()?.Template.StartsWith("api/audit", StringComparison.OrdinalIgnoreCase) == true)
            .SelectMany(t => t.GetMethods().SelectMany(m => m.GetCustomAttributes<HttpMethodAttribute>()))
            .SelectMany(a => a.HttpMethods)
            .Where(m => m != "GET");

        Assert.Empty(writing);
    }

    [Fact]
    public void The_service_can_only_add_entries()
    {
        var names = typeof(IAuditTrailService).GetMethods().Select(m => m.Name).ToList();

        Assert.Equal(["RecordBatchOpenedAsync", "SearchAsync"], names.Order());
    }

    private static AuditEntry Entry() => new()
    {
        OccurredAt = new DateTime(2026, 10, 13, 6, 0, 0, DateTimeKind.Utc),
        Actor = "qco-1",
        Action = AuditActions.BatchTraceOpened,
        EntityType = AuditTrailService.BatchEntityType,
        EntityId = "262-FM-B",
    };

    // Each test class instance has its own in-memory database; every context opened here shares it.
    private readonly string _database = Guid.NewGuid().ToString();

    private TraceabilityDbContext NewContext() =>
        new(new DbContextOptionsBuilder<TraceabilityDbContext>().UseInMemoryDatabase(_database).Options);
}

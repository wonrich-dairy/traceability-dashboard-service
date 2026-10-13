using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TraceabilityService.Api.Infrastructure.Persistence;
using TraceabilityService.Application;
using TraceabilityService.Domain.Events;
using TraceabilityService.Infrastructure.Kafka;

namespace TraceabilityService.UnitTests;

public class ConsignmentTestedHandlerTests
{
    [Fact]
    public async Task Accepted_and_rejected_tests_update_the_same_daily_society_aggregate()
    {
        var options = new DbContextOptionsBuilder<TraceabilityDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TraceabilityDbContext(options);
        var handler = new ConsignmentTestedHandler(db, TimeProvider.System);
        var occurredAt = new DateTime(2026, 10, 12, 19, 0, 0, DateTimeKind.Utc);

        await Apply(handler, db, new ConsignmentTestedEvent
        {
            Society = new SocietyDetails { Code = "VS014", Name = "Valley Society" },
            Verdict = "Accept",
            OccurredAtUtc = occurredAt,
            IsMarginal = true,
            Panel = new QualityPanelDetails { FatPercent = 3.7m, Snf = 8.5m },
        }, 1);
        await Apply(handler, db, new ConsignmentTestedEvent
        {
            Society = new SocietyDetails { Code = "VS014", Name = "Valley Society" },
            Verdict = "Reject",
            OccurredAtUtc = occurredAt.AddMinutes(1),
            Panel = new QualityPanelDetails { FatPercent = 2.9m, Snf = 7.8m },
        }, 2);

        var aggregate = await db.DailySocietyAggregates.SingleAsync();
        Assert.Equal(new DateOnly(2026, 10, 13), aggregate.Date); // Factory-local date is UTC+05:30.
        Assert.Equal("VS014", aggregate.SocietyCode);
        Assert.Equal(2, aggregate.TotalConsignments);
        Assert.Equal(1, aggregate.AcceptedCount);
        Assert.Equal(1, aggregate.RejectedCount);
        Assert.Equal(6.6m, aggregate.FatPercentTotal);
        Assert.Equal(16.3m, aggregate.SnfTotal);
        Assert.Equal(1, aggregate.MarginalCount);
    }

    private static async Task Apply(
        ConsignmentTestedHandler handler,
        TraceabilityDbContext db,
        ConsignmentTestedEvent value,
        int offset)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await handler.HandleAsync(new ConsumedEvent(
            "wonrich.mcc.consignment-tested.v1", 0, offset, "VS014", "ConsignmentTested",
            Guid.NewGuid(), "test-correlation", json), CancellationToken.None);
        await db.SaveChangesAsync();
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TraceabilityService.Api.Infrastructure.Persistence;
using TraceabilityService.Domain.Entities;
using TraceabilityService.Domain.Events;
using TraceabilityService.Infrastructure.Kafka;

namespace TraceabilityService.Application;

public static class ConsignmentTestedConsumer
{
    public const string Name = "ConsignmentTested";

    public static IServiceCollection AddConsignmentTestedConsumer(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        return services.AddEventConsumer(
            KafkaConsumerOptions.From(configuration, Name,
                topic: "wonrich.intake.consignment-tested.v1",
                consumerGroup: "traceability-consignment-tested",
                deadLetterTopic: "wonrich.dlq.traceability-consignment-tested.v1"),
            handlers => handlers.Add<ConsignmentTestedHandler>("ConsignmentTested"));
    }
}

/// <summary>Projects one gate verdict into the society aggregate inside the event transaction.</summary>
public sealed class ConsignmentTestedHandler(
    TraceabilityDbContext db,
    TimeProvider time) : TypedEventHandler<ConsignmentTestedEvent>
{
    protected override async Task HandleAsync(
        ConsignmentTestedEvent tested, ConsumedEvent consumed, CancellationToken cancellationToken)
    {
        var societyCode = tested.Society?.Code?.Trim();
        if (string.IsNullOrWhiteSpace(societyCode))
            throw new InvalidDataException("ConsignmentTested event is missing society.code.");
        if (tested.Panel is null)
            throw new InvalidDataException("ConsignmentTested event is missing panel values.");

        var accepted = string.Equals(tested.Verdict, "Accept", StringComparison.OrdinalIgnoreCase)
            || string.Equals(tested.Verdict, "Accepted", StringComparison.OrdinalIgnoreCase);
        var rejected = string.Equals(tested.Verdict, "Reject", StringComparison.OrdinalIgnoreCase)
            || string.Equals(tested.Verdict, "Rejected", StringComparison.OrdinalIgnoreCase);
        if (!accepted && !rejected)
            throw new InvalidDataException($"Unknown ConsignmentTested verdict '{tested.Verdict}'.");

        var occurredAt = tested.OccurredAtUtc == default
            ? time.GetUtcNow().UtcDateTime
            : DateTime.SpecifyKind(tested.OccurredAtUtc, DateTimeKind.Utc);
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(occurredAt, FactoryCalendar.Zone));
        var aggregate = await db.DailySocietyAggregates.SingleOrDefaultAsync(
            row => row.Date == date && row.SocietyCode == societyCode, cancellationToken);

        if (aggregate is null)
        {
            aggregate = new DailySocietyAggregate { Date = date, SocietyCode = societyCode };
            db.DailySocietyAggregates.Add(aggregate);
        }

        aggregate.SocietyName = tested.Society?.Name?.Trim() ?? aggregate.SocietyName;
        aggregate.TotalConsignments++;
        if (accepted) aggregate.AcceptedCount++;
        if (rejected) aggregate.RejectedCount++;
        aggregate.FatPercentTotal += tested.Panel.FatPercent;
        aggregate.SnfTotal += tested.Panel.Snf;
        if (tested.IsMarginal) aggregate.MarginalCount++;
        aggregate.UpdatedAt = time.GetUtcNow().UtcDateTime;

        // The unit of work only commits the transaction; like every other handler, this one saves
        // its own changes, or the aggregate is dropped while the message is still marked processed.
        await db.SaveChangesAsync(cancellationToken);
    }
}

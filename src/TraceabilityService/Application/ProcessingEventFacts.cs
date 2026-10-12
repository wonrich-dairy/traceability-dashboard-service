using System.Globalization;
using System.Text.RegularExpressions;
using Wonrich.Processing.Contracts.Events;

namespace TraceabilityService.Application;

/// <summary>
/// What one Processing event means for the read model (SCRUM-130): its timeline entry, its
/// deviation if it has one, and what it says about holds and completion. Pure, so the
/// mapping of each of the five event types is unit-tested without a database.
/// </summary>
public sealed partial record ProcessingEventFacts
{
    // The record name without its Event suffix, as timeline_events stores it (docs/schema.md).
    public required string EventType { get; init; }

    // When it happened upstream, not TimestampUtc (when the event was created).
    public required DateTime OccurredAt { get; init; }

    public string? RecordedBy { get; init; }

    public string? HoldRaised { get; init; }

    public string? HoldResolved { get; init; }

    public bool Completed { get; init; }

    public ProcessingDeviation? Deviation { get; init; }

    public static ProcessingEventFacts Of(ProcessingEventBase processingEvent) => processingEvent switch
    {
        MilkAllocatedToMixingTankEvent e => new()
        {
            EventType = Name(e), OccurredAt = e.AllocatedAtUtc, RecordedBy = e.AllocatedBy,
        },
        ProcessingStageRecordedEvent e => new()
        {
            EventType = Name(e), OccurredAt = e.StartTimeUtc, RecordedBy = e.RecordedBy,
            Deviation = e.IsDeviation
                ? new ProcessingDeviation("EndTemperatureC", e.EndTemperatureC, $"{e.StageType} end temperature out of range")
                : null,
        },
        // Its IsDeviation says whether any stage of the run deviated. Those stages have their
        // own rows already, so a row here would count each of them twice.
        ProcessingCompletedEvent e => new()
        {
            EventType = Name(e), OccurredAt = e.CompletedAtUtc, RecordedBy = e.CompletedBy, Completed = true,
        },
        ProcessingHoldRaisedEvent e => new()
        {
            EventType = Name(e), OccurredAt = e.RaisedAtUtc, RecordedBy = e.RaisedBy, HoldRaised = e.HoldId,
            Deviation = e.IsDeviation
                ? new ProcessingDeviation(e.FailedParameter, LeadingNumber(e.FailedValue), Truncate(e.Reason, 500))
                : null,
        },
        ProcessingHoldResolvedEvent e => new()
        {
            EventType = Name(e), OccurredAt = e.ResolvedAtUtc, RecordedBy = e.ResolvedBy, HoldResolved = e.HoldId,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(processingEvent), processingEvent.GetType().Name, "Not a Processing event type."),
    };

    private static string Name(ProcessingEventBase processingEvent)
    {
        var name = processingEvent.GetType().Name;
        return name.EndsWith("Event", StringComparison.Ordinal) ? name[..^"Event".Length] : name;
    }

    // FailedValue is text such as "2.8 < 3.0 min" or "Not OK": the reading is the number it
    // starts with, if any. The whole text stays in the payload and the description.
    private static decimal? LeadingNumber(string? text) =>
        text is not null
        && LeadingNumberPattern().Match(text) is { Success: true } match
        && decimal.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        && Math.Abs(value) < 1000m // decimal(5,2) holds up to 999.99
            ? value
            : null;

    private static string? Truncate(string? text, int length) =>
        text is null || text.Length <= length ? text : text[..length];

    [GeneratedRegex(@"^\s*-?\d+(\.\d+)?")]
    private static partial Regex LeadingNumberPattern();
}

/// <summary>A deviation_records row at the Processing checkpoint. No reason code: Processing sends none.</summary>
public sealed record ProcessingDeviation(string? Parameter, decimal? ObservedValue, string? Description);

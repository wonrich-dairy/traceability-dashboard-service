using TraceabilityService.Domain.Entities;
using TraceabilityService.Infrastructure.MccIntake;
using static TraceabilityService.Domain.Entities.UpstreamSnapshotPayload;

namespace TraceabilityService.Application;

/// <summary>
/// Turns the MCC and Intake Service's answers into an <see cref="UpstreamLookup"/>: the
/// <see cref="UpstreamSnapshotPayload"/> when the tree is whole, Incomplete when it has gaps.
/// The field mapping is the table under upstream_snapshots.Payload in docs/schema.md. Pure, so
/// it is unit-tested without HTTP.
/// </summary>
public static class UpstreamSnapshotMapper
{
    /// <summary>
    /// The batch factory intake created from the dispatch note. Intake screens a dispatch note
    /// once, so there is at most one; if there are more, the first, which the service's list
    /// puts newest. Nothing here sorts what the service sends: tanks and consignments are
    /// stored in its order.
    /// </summary>
    public static BatchView? IntakeBatchOf(IReadOnlyList<BatchView> batches) => batches.FirstOrDefault();

    /// <param name="intake">The batch from the lookup by dispatch note: the arrival temperature comes from it.</param>
    /// <param name="trace">That batch traced back to its tanks and consignments.</param>
    public static UpstreamLookup Map(BatchView intake, BatchTraceView trace)
    {
        var gaps = Gaps(trace);
        if (gaps.Count > 0)
        {
            return UpstreamLookup.Incomplete(
                $"MCC and Intake Service could not resolve all of batch {trace.BatchReference}: {string.Join(" ", gaps)}");
        }

        return UpstreamLookup.Found(new UpstreamSnapshotPayload
        {
            BatchReference = trace.BatchReference,
            Dispatch = new DispatchPart
            {
                Reference = trace.DispatchNoteReference,
                DispatchedAt = FromFactoryLocal(trace.DispatchedAtLocal),
                DispatchedBy = NullIfEmpty(trace.DispatchedBy),
                BowserRegistration = NullIfEmpty(trace.BowserRegistration),
                TotalLitres = trace.TotalDispatchedLitres,
            },
            Intake = new IntakePart
            {
                ArrivedAt = FromFactoryLocal(trace.ArrivedAtLocal),
                ScreenedAt = AsUtc(trace.ScreenedAtUtc),
                ScreenedBy = NullIfEmpty(trace.ScreenedBy),
                TemperatureC = intake.ArrivalTemperatureCelsius,
            },
            Tanks = [.. trace.Tanks.Select(tank => new TankPart
            {
                Code = tank.TankCode,
                QuantityDrawnLitres = tank.QuantityDrawnLitres,
                Consignments = [.. tank.Consignments.Select(Consignment)],
            })],
        });
    }

    // What the service listed as unresolved, for the batch and for each tank, and any consignment
    // whose society it could not resolve: society.code is required in the payload. A consignment
    // without a gate test is not a gap; its panel is left out, as the payload allows.
    private static List<string> Gaps(BatchTraceView trace)
    {
        var gaps = new List<string>(trace.Missing);
        foreach (var tank in trace.Tanks)
        {
            gaps.AddRange(tank.Missing.Select(m => $"Tank {Label(tank.TankCode)}: {m}"));
            gaps.AddRange(tank.Consignments
                .Where(c => string.IsNullOrEmpty(c.SocietyCode))
                .Select(c => $"Consignment {c.Reference}: the supplying society could not be resolved."));
        }

        return gaps;
    }

    private static ConsignmentPart Consignment(TracedConsignmentView consignment) => new()
    {
        Reference = consignment.Reference,
        Society = new SocietyPart { Code = consignment.SocietyCode, Name = NullIfEmpty(consignment.SocietyName) },
        VolumeLitres = consignment.QuantityLitres,
        RegisteredAt = AsUtc(consignment.RegisteredAtUtc),
        RegisteredBy = NullIfEmpty(consignment.RegisteredBy),
        Panel = consignment.QualityTest is { } test ? Panel(test, consignment.TightestMargin) : null,
    };

    private static PanelPart Panel(TracedQualityTestView test, decimal tightestMargin)
    {
        // The service's -1 placeholder is not copied: an unscored panel has no margin.
        var scored = tightestMargin != MarginToThreshold.Unknown;
        return new PanelPart
        {
            FatPercentage = test.FatPercent,
            SnfPercentage = test.Snf,
            ClrReading = test.CorrectedClr,
            TemperatureC = test.TemperatureCelsius,
            Verdict = test.Verdict,
            TightestMargin = scored ? tightestMargin : null,
            // Margins is tightest first.
            TightestMeasure = scored ? test.Margins.FirstOrDefault()?.Measure : null,
        };
    }

    // The service's …Local fields are factory wall-clock time, sent without an offset.
    private static DateTime FromFactoryLocal(DateTime local) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), FactoryCalendar.Zone);

    // Its …Utc fields may arrive without a Z, and are read back as Unspecified.
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string Label(string tankCode) => string.IsNullOrEmpty(tankCode) ? "(unresolved)" : tankCode;
}

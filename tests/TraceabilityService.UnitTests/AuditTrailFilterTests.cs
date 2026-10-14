using TraceabilityService.Api.Contracts;
using TraceabilityService.Application;

namespace TraceabilityService.UnitTests;

/// <summary>
/// The audit trail's filters (SCRUM-138): checking the query, then what each filter keeps, on
/// their own and together. Dates are factory days in Asia/Colombo (UTC+5:30). Case insensitivity
/// comes from MySQL's collation, so it is tested in AuditTrailEndpointTests instead.
/// </summary>
public class AuditTrailFilterTests
{
    private static readonly DateTime Day19 = new(2026, 9, 19, 0, 0, 0, DateTimeKind.Utc);

    // Six entries over three Colombo days, three users, three batches and every source.
    private static readonly AuditTrailRow[] Rows =
    [
        Row("event-1", Day19.AddHours(5), "proc.tech-1", "ProcessingStageRecorded", "262-FM-A"),
        Row("event-2", Day19.AddHours(9), "analyst-1", "BatchFailed", "262-FM-A"),
        Row("consignment-1-1", Day19.AddHours(-6), "mcc.officer-1", "ConsignmentRegistered", "262-FM-A"),   // 18 Sept 23:30 in Colombo
        Row("event-3", Day19.AddDays(1).AddHours(5), "proc.tech-1", "ProcessingHoldRaised", "263-SY-A"),
        Row("audit-1", Day19.AddDays(1).AddHours(19), "qco-1", "BatchTraceOpened", "263-SY-A"),              // 21 Sept 00:30 in Colombo
        Row("audit-2", Day19.AddDays(2).AddHours(6), "qco-1", "BatchSearchExported", null),
    ];

    [Fact]
    public void With_no_parameters_nothing_is_filtered()
    {
        var (filter, errors) = AuditTrailFilter.Resolve(new AuditTrailQuery());

        Assert.Empty(errors);
        Assert.Equal(new AuditTrailFilter(), filter);
        Assert.Equal(Rows.Length, filter!.Apply(Rows.AsQueryable()).Count());
    }

    [Fact]
    public void Text_filters_are_trimmed_and_blank_ones_ignored()
    {
        var (filter, errors) = AuditTrailFilter.Resolve(new AuditTrailQuery { User = "  qco-1 ", Action = " ", BatchId = "\t262-FM-A" });

        Assert.Empty(errors);
        Assert.Equal(new AuditTrailFilter(User: "qco-1", BatchId: "262-FM-A"), filter);
    }

    [Fact]
    public void From_after_to_is_rejected()
    {
        var (filter, errors) = AuditTrailFilter.Resolve(new AuditTrailQuery { From = new(2026, 9, 20), To = new(2026, 9, 19) });

        Assert.Null(filter);
        Assert.Contains("from", errors.Keys);
    }

    [Fact]
    public void The_same_day_for_from_and_to_is_one_day()
    {
        var (filter, errors) = AuditTrailFilter.Resolve(new AuditTrailQuery { From = new(2026, 9, 19), To = new(2026, 9, 19) });

        Assert.Empty(errors);
        Assert.Equal(["event-1", "event-2"], Ids(filter!));
    }

    [Theory]
    [InlineData("user")]
    [InlineData("action")]
    [InlineData("batchId")]
    public void Text_longer_than_any_stored_value_is_rejected(string parameter)
    {
        var tooLong = new string('x', AuditTrailFilter.MaxTextLength + 1);
        var query = parameter switch
        {
            "user" => new AuditTrailQuery { User = tooLong },
            "action" => new AuditTrailQuery { Action = tooLong },
            _ => new AuditTrailQuery { BatchId = tooLong },
        };

        var (filter, errors) = AuditTrailFilter.Resolve(query);

        Assert.Null(filter);
        Assert.Contains(parameter, errors.Keys);
    }

    [Fact]
    public void Every_invalid_parameter_is_reported_at_once()
    {
        var (_, errors) = AuditTrailFilter.Resolve(new AuditTrailQuery
        {
            User = new string('x', 101),
            From = new(2026, 9, 2),
            To = new(2026, 9, 1),
        });

        Assert.Equal(["from", "user"], errors.Keys.Order());
    }

    [Fact]
    public void User_keeps_only_that_users_entries_from_every_source()
    {
        Assert.Equal(["event-1", "event-3"], Ids(new AuditTrailFilter(User: "proc.tech-1")));
        Assert.Equal(["consignment-1-1"], Ids(new AuditTrailFilter(User: "mcc.officer-1")));
        Assert.Equal(["audit-1", "audit-2"], Ids(new AuditTrailFilter(User: "qco-1")));
    }

    [Fact]
    public void Action_keeps_only_that_action()
    {
        Assert.Equal(["audit-1"], Ids(new AuditTrailFilter(Action: "BatchTraceOpened")));
        Assert.Equal(["event-2"], Ids(new AuditTrailFilter(Action: "BatchFailed")));
    }

    [Fact]
    public void The_date_range_is_factory_days_in_colombo()
    {
        // 23:30 on 18 Sept in Colombo is before the 19th, though 19 Sept in UTC.
        Assert.Equal(["consignment-1-1"], Ids(new AuditTrailFilter(To: new(2026, 9, 18))));

        // 00:30 on 21 Sept in Colombo is after the 20th, though 20 Sept in UTC.
        Assert.Equal(["event-3"], Ids(new AuditTrailFilter(From: new(2026, 9, 20), To: new(2026, 9, 20))));
        Assert.Equal(["audit-1", "audit-2"], Ids(new AuditTrailFilter(From: new(2026, 9, 21))));
    }

    [Fact]
    public void Batch_keeps_only_that_batch_and_never_an_export()
    {
        Assert.Equal(["event-1", "event-2", "consignment-1-1"], Ids(new AuditTrailFilter(BatchId: "262-FM-A")));
        Assert.Equal(["event-3", "audit-1"], Ids(new AuditTrailFilter(BatchId: "263-SY-A")));
    }

    [Fact]
    public void Filters_combine_with_and()
    {
        Assert.Equal(["event-3"], Ids(new AuditTrailFilter(User: "proc.tech-1", BatchId: "263-SY-A")));
        Assert.Equal(["event-1"], Ids(new AuditTrailFilter(User: "proc.tech-1", From: new(2026, 9, 19), To: new(2026, 9, 19))));
        Assert.Empty(Ids(new AuditTrailFilter(User: "analyst-1", Action: "BatchTraceOpened")));
    }

    [Fact]
    public void A_filter_that_matches_nothing_is_valid()
    {
        var (filter, errors) = AuditTrailFilter.Resolve(new AuditTrailQuery { User = "nobody" });

        Assert.Empty(errors);
        Assert.Empty(Ids(filter!));
    }

    private static List<string> Ids(AuditTrailFilter filter) =>
        [.. filter.Apply(Rows.AsQueryable()).Select(r => r.EntryId)];

    private static AuditTrailRow Row(string id, DateTime at, string user, string action, string? batchId) => new()
    {
        Source = id.StartsWith("event") ? "ConsumedEvent" : id.StartsWith("audit") ? "Traceability" : "UpstreamSnapshot",
        EntryId = id,
        OccurredAt = at,
        UserId = user,
        Action = action,
        BatchId = batchId,
    };
}

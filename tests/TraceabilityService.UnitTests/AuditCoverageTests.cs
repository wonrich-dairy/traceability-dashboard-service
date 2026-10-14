using TraceabilityService.Api.Contracts;
using TraceabilityService.Api.Infrastructure.Persistence;
using TraceabilityService.Application;
using TraceabilityService.Domain.Entities;
using Wonrich.Processing.Contracts.Events;

namespace TraceabilityService.UnitTests;

/// <summary>
/// The coverage list (SCRUM-138) names every action the audit trail can show, under the name the
/// trail shows it, and says what is not covered.
/// </summary>
public class AuditCoverageTests
{
    [Fact]
    public void Every_consumed_processing_event_is_listed_under_its_timeline_name()
    {
        // The timeline stores Processing's type name without "Event" (ProcessingEventFacts).
        string[] stored =
        [
            .. new[]
            {
                typeof(MilkAllocatedToMixingTankEvent), typeof(ProcessingStageRecordedEvent), typeof(ProcessingCompletedEvent),
                typeof(ProcessingHoldRaisedEvent), typeof(ProcessingHoldResolvedEvent),
            }.Select(t => t.Name[..^"Event".Length]),
        ];

        Assert.All(stored, action => Assert.Contains(Covered(AuditSource.ConsumedEvent, Checkpoint.Processing), c => c.Action == action));
    }

    [Fact]
    public void Both_lab_determinations_are_listed()
    {
        var lab = Covered(AuditSource.ConsumedEvent, Checkpoint.Lab).Select(c => c.Action);

        Assert.Equal(["BatchCleared", "BatchFailed"], lab.Order());
    }

    [Fact]
    public void Every_upstream_snapshot_record_is_listed_at_its_checkpoint()
    {
        Assert.Equal(
            [AuditActions.ConsignmentRegistered, AuditActions.MilkDispatched],
            Covered(AuditSource.UpstreamSnapshot, Checkpoint.Mcc).Select(c => c.Action).Order());
        Assert.Equal(
            [AuditActions.FactoryIntakeScreened],
            Covered(AuditSource.UpstreamSnapshot, Checkpoint.Intake).Select(c => c.Action));
    }

    [Fact]
    public void Opening_a_batch_and_exporting_are_listed_as_traceabilitys_own_with_no_checkpoint()
    {
        var own = AuditCoverage.Covered.Where(c => c.Source == AuditSource.Traceability).ToList();

        Assert.Equal(
            [AuditActions.BatchRecordOpened, AuditActions.BatchSearchExported, AuditActions.BatchTraceOpened, AuditActions.DeadLetterReplayed, AuditActions.ProjectionRebuilt],
            own.Select(c => c.Action).Order());
        Assert.All(own, c => Assert.Null(c.Checkpoint));
        Assert.Equal(BatchSearchService.ExportAction, AuditActions.BatchSearchExported);
    }

    [Fact]
    public void Each_action_is_listed_once_and_described()
    {
        Assert.Equal(AuditCoverage.Covered.Count, AuditCoverage.Covered.Select(c => c.Action).Distinct().Count());
        Assert.All(AuditCoverage.Covered, c => Assert.False(string.IsNullOrWhiteSpace(c.Description)));
    }

    [Fact]
    public void What_is_not_covered_is_listed_with_a_reason()
    {
        Assert.NotEmpty(AuditCoverage.NotCovered);
        Assert.All(AuditCoverage.NotCovered, gap =>
        {
            Assert.False(string.IsNullOrWhiteSpace(gap.Action));
            Assert.False(string.IsNullOrWhiteSpace(gap.Reason));
        });
        Assert.Contains(AuditCoverage.NotCovered, gap => gap.Action.Contains("without an event", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_dto_carries_both_lists_and_points_at_the_document()
    {
        var dto = AuditCoverage.Dto();

        Assert.Equal(AuditCoverage.Covered.Count, dto.Covered.Count);
        Assert.Equal(AuditCoverage.NotCovered.Count, dto.NotCovered.Count);
        Assert.Equal("docs/audit-trail.md", dto.Documentation);
    }

    [Theory]
    [InlineData("CREATE TRIGGER IF NOT EXISTS t BEFORE UPDATE ON audit_entries FOR EACH ROW SIGNAL SQLSTATE '45000';", true)]
    [InlineData("\n  create trigger t before delete on x for each row set @a = 1;", true)]
    [InlineData("DROP TRIGGER IF EXISTS audit_entries_no_update;", true)]
    [InlineData("CREATE INDEX `IX_a` ON `audit_entries` (`Actor`);", false)]
    [InlineData("INSERT INTO `__EFMigrationsHistory` VALUES ('x', '9.0.0');", false)]
    [InlineData("ALTER TABLE t ADD COLUMN `TriggeredBy` varchar(10);", false)]
    public void The_migrator_lifts_only_trigger_statements_out_of_the_script_guards(string sql, bool lifted)
    {
        Assert.Equal(lifted, TriggerAwareMySqlMigrator.IsTriggerStatement(sql));
    }

    private static IEnumerable<CoveredActionDto> Covered(AuditSource source, Checkpoint checkpoint) =>
        AuditCoverage.Covered.Where(c => c.Source == source && c.Checkpoint == checkpoint);
}

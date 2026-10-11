using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Api.Contracts;

/// <summary>
/// What every dashboard response carries: the filters it was answered for, and how fresh
/// the figures are.
/// </summary>
public abstract class DashboardResponse
{
    // The latest processed_messages.ProcessedAt: when the read model last took in an
    // event. Null if nothing has been processed yet.
    public DateTime? DataAsOf{get; set;}

    public string? Facility{get; set;}
    public string? ProductLine{get; set;}

    // The production days covered, both ends inclusive.
    public DateOnly From{get; set;}
    public DateOnly To{get; set;}
}

public class StatusCountsDto
{
    public int Pending{get; set;}
    public int Cleared{get; set;}
    public int Failed{get; set;}
    public int OnHold{get; set;}
}

public class TodayCountsDto : StatusCountsDto
{
    // Today in Asia/Colombo.
    public DateOnly Date{get; set;}
}

public class CheckpointDeviationDto
{
    public Checkpoint Checkpoint{get; set;}
    public int Count{get; set;}
}

/// <summary>GET /api/dashboard/summary.</summary>
public class DashboardSummaryDto : DashboardResponse
{
    public required TodayCountsDto Today{get; set;}
    public required StatusCountsDto Range{get; set;}

    // Deviations over the range by the checkpoint that reported them: always four, in
    // stage order.
    public List<CheckpointDeviationDto> Deviations{get; set;} = [];
}

public class TrendPointDto
{
    public DateOnly Date{get; set;}
    public required string ProductLine{get; set;}
    public int BatchCount{get; set;}
    public int ClearedCount{get; set;}
    public int FailedCount{get; set;}

    // Failed / (cleared + failed), a fraction from 0 to 1. Null when nothing was decided
    // that day: there is no rate, and 0 would read as "nothing failed".
    public double? FailRate{get; set;}
}

/// <summary>GET /api/dashboard/trend.</summary>
public class DashboardTrendDto : DashboardResponse
{
    // One per day and product line that has data, by date then product line.
    public List<TrendPointDto> Points{get; set;} = [];
}

public class FailureReasonDto
{
    public required string ReasonCode{get; set;}
    public int Count{get; set;}
}

/// <summary>GET /api/dashboard/failure-reasons.</summary>
public class DashboardFailureReasonsDto : DashboardResponse
{
    // Most frequent first.
    public List<FailureReasonDto> Reasons{get; set;} = [];
}

public class RecentFailureDto
{
    public required string BatchId{get; set;}
    public string? BatchReference{get; set;}
    public required string ProductLine{get; set;}
    public required string Facility{get; set;}

    // The batch's latest event: when the failure was recorded.
    public DateTime FailedAt{get; set;}

    // The batch's deviation reasons, without repeats.
    public List<string> ReasonCodes{get; set;} = [];

    // The batch record, e.g. /api/batches/262-FM-B.
    public required string TracePath{get; set;}
}

/// <summary>GET /api/dashboard/recent-failures.</summary>
public class DashboardRecentFailuresDto : DashboardResponse
{
    // Newest first.
    public List<RecentFailureDto> Batches{get; set;} = [];
}

public class MarginalSocietyDto
{
    public required string SocietyCode{get; set;}
    public string? SocietyName{get; set;}

    // Distinct consignments, and the batches in range they went into. One consignment can
    // feed two batches when Processing splits a dispatch, and counts once here.
    public int ConsignmentCount{get; set;}
    public int BatchCount{get; set;}

    // The closest any of its consignments came to a rejection limit, as a fraction of the
    // limit, and on which measure. Null when none of them could be scored.
    public decimal? TightestMargin{get; set;}
    public string? TightestMeasure{get; set;}
}

/// <summary>GET /api/dashboard/societies.</summary>
public class DashboardSocietiesDto : DashboardResponse
{
    public const string UpstreamSnapshots = "upstream snapshots";
    public const string ContributorsInRange = "societies that contributed to a batch in range";

    // Where the figures come from and what they cover, so the screen can state the
    // limitation: per-society quality is not kept here (TR-12 is descoped), only what the
    // cached snapshots of batches in the range say.
    public string Source{get; set;} = UpstreamSnapshots;
    public string Coverage{get; set;} = ContributorsInRange;

    // Most marginal first; those with no score last.
    public List<MarginalSocietyDto> Societies{get; set;} = [];
}

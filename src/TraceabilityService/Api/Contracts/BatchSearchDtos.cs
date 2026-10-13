using System.Text.Json.Serialization;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Api.Contracts;

/// <summary>
/// One page of GET /api/batches/search (SCRUM-139): the matching batches, newest first.
/// </summary>
public class BatchSearchPageDto
{
    /// <summary>
    /// At most <see cref="PageSize"/> rows. Each batch appears once, however many tanks,
    /// societies, users or reasons it has.
    /// </summary>
    public List<BatchSearchRowDto> Items{get; set;} = [];

    /// <summary>
    /// The page returned, from 1.
    /// </summary>
    public int Page{get; set;}

    public int PageSize{get; set;}

    /// <summary>
    /// Every batch matching the filters, across all pages.
    /// </summary>
    public int TotalCount{get; set;}

    /// <summary>
    /// 0 when nothing matches.
    /// </summary>
    public int TotalPages{get; set;}

    /// <summary>
    /// Said when nothing matches, so a client can show it as it is. Null otherwise.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Message{get; set;}

    /// <summary>
    /// The filters as applied, normalised (product line in upper case, status by name).
    /// </summary>
    public required BatchSearchFiltersDto Filters{get; set;}
}

/// <summary>
/// The filters a search or export ran with. Null for a filter that was not given.
/// </summary>
public class BatchSearchFiltersDto
{
    public BatchStatus? Status{get; set;}
    public string? ProductLine{get; set;}
    public DateOnly? From{get; set;}
    public DateOnly? To{get; set;}
    public string? Society{get; set;}
    public string? Tank{get; set;}
    public string? User{get; set;}
    public string? ReasonCode{get; set;}
    public string? Facility{get; set;}
}

/// <summary>
/// One batch as the search lists it and the export writes it: one row per batch.
/// </summary>
public class BatchSearchRowDto : BatchSummaryDto
{
    /// <summary>
    /// The upstream snapshot's status; null when the batch has no snapshot row yet. Tanks,
    /// societies and the MCC and intake users are only known once it is Resolved.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public ResolutionStatus? UpstreamStatus{get; set;}

    /// <summary>
    /// The MCC tanks the batch drew from, in the snapshot's order.
    /// </summary>
    public List<string> Tanks{get; set;} = [];

    /// <summary>
    /// Each society behind the batch once, in the snapshot's order.
    /// </summary>
    public List<SocietyLinkDto> Societies{get; set;} = [];

    /// <summary>
    /// Everyone who recorded something on the batch, sorted.
    /// </summary>
    public List<string> Users{get; set;} = [];

    /// <summary>
    /// Deviations at every checkpoint, superseded lab determinations' included.
    /// </summary>
    public int DeviationCount{get; set;}

    /// <summary>
    /// The deviations' reason codes, each once, sorted.
    /// </summary>
    public List<string> ReasonCodes{get; set;} = [];

    /// <summary>
    /// Always the same five, in the same order: FatPercent, Ph, SNF, CorrectedCLR, Texture. A
    /// parameter the batch's product line does not measure is NotApplicable, never left out.
    /// </summary>
    public List<LabParameterDto> LabParameters{get; set;} = [];
}

/// <summary>
/// One Quality Lab parameter for one batch, judged by its current determination.
/// </summary>
public class LabParameterDto
{
    /// <summary>
    /// FatPercent, Ph, SNF, CorrectedCLR or Texture.
    /// </summary>
    public required string Parameter{get; set;}

    public LabParameterState State{get; set;}

    /// <summary>
    /// The reading, when OutOfSpec and the lab sent one.
    /// </summary>
    public decimal? ObservedValue{get; set;}

    /// <summary>
    /// The limit it broke, when OutOfSpec and the lab sent one.
    /// </summary>
    public decimal? Limit{get; set;}
}

public enum LabParameterState
{
    /// <summary>
    /// The product line does not measure it, e.g. SNF on a set yoghurt.
    /// </summary>
    NotApplicable,

    /// <summary>
    /// No current determination yet.
    /// </summary>
    Pending,

    /// <summary>
    /// The current determination records no deviation on it.
    /// </summary>
    NoDeviation,

    /// <summary>
    /// The current determination records it out of specification.
    /// </summary>
    OutOfSpec
}

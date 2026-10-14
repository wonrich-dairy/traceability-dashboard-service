using Microsoft.EntityFrameworkCore;
using TraceabilityService.Api.Contracts;
using TraceabilityService.Api.Infrastructure.Persistence;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Application;

/// <summary>
/// Loads what a backward trace needs (SCRUM-134) and hands it to BackwardTraceAssembler.
/// Five queries whatever the batch holds, one per table, each on the table's BatchId
/// index; an unknown batch stops after the first. Nothing is reached through navigation
/// properties, so a batch with many events or deviations costs no extra round trips.
/// </summary>
public class BatchTraceService(TraceabilityDbContext db) : IBatchTraceService
{
    private readonly TraceabilityDbContext _db = db;

    public async Task<BatchTraceResponse?> GetAsync(
        string batchId,
        CancellationToken cancellationToken = default)
    {
        var batch = await _db.BatchRecords
            .AsNoTracking()
            .Where(b => b.BatchId == batchId)
            .Select(b => new BatchRecord
            {
                BatchId = b.BatchId,
                DispatchRef = b.DispatchRef,
                BatchReference = b.BatchReference,
                ProductLine = b.ProductLine,
                Facility = b.Facility,
                Status = b.Status,
                Completeness = b.Completeness,
                FirstEventAt = b.FirstEventAt,
                LastEventAt = b.LastEventAt,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (batch is null)
        {
            return null;
        }

        // Only hold events' payloads are read, so the others' are not fetched.
        var events = await _db.TimelineEvents
            .AsNoTracking()
            .Where(e => e.BatchId == batchId)
            .Select(e => new TimelineEvent
            {
                Id = e.Id,
                BatchId = e.BatchId,
                Checkpoint = e.Checkpoint,
                EventType = e.EventType,
                OccurredAt = e.OccurredAt,
                RecordedBy = e.RecordedBy,
                Payload = e.EventType == BackwardTraceAssembler.HoldRaised || e.EventType == BackwardTraceAssembler.HoldResolved
                    ? e.Payload
                    : "{}",
                SourceEventId = e.SourceEventId,
                ContractVersion = e.ContractVersion,
            })
            .ToListAsync(cancellationToken);

        var deviations = await _db.DeviationRecords
            .AsNoTracking()
            .Where(d => d.BatchId == batchId)
            .ToListAsync(cancellationToken);

        // Superseded ones are left out: only the active determination sets the lab result.
        var determinations = await _db.LabDeterminations
            .AsNoTracking()
            .Where(d => d.BatchId == batchId && !d.IsSuperseded)
            .Select(d => new LabDetermination
            {
                DeterminationId = d.DeterminationId,
                BatchId = d.BatchId,
                Result = d.Result,
                DeterminedAt = d.DeterminedAt,
                DeterminedBy = d.DeterminedBy,
                IsSuperseded = d.IsSuperseded,
            })
            .ToListAsync(cancellationToken);

        var snapshot = await _db.UpstreamSnapshots
            .AsNoTracking()
            .Where(s => s.BatchId == batchId)
            .Select(s => new UpstreamSnapshot
            {
                BatchId = s.BatchId,
                ResolutionStatus = s.ResolutionStatus,
                Payload = s.Payload,
                FailureReason = s.FailureReason,
            })
            .SingleOrDefaultAsync(cancellationToken);

        return BackwardTraceAssembler.Assemble(batch, events, deviations, determinations, snapshot);
    }
}

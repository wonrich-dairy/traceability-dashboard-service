using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using TraceabilityService.Api.Contracts;
using TraceabilityService.Api.Infrastructure.Persistence;
using TraceabilityService.Domain.Entities;

namespace TraceabilityService.Application;

public class BatchRecordService(TraceabilityDbContext db) : IBatchRecordService
{
    private readonly TraceabilityDbContext _db = db;

    public async Task<BatchRecordDto?> GetAsync(
        string batchId,
        CancellationToken cancellationToken = default)
    {
        var batch = await _db.BatchRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(b => b.BatchId == batchId, cancellationToken);

        if (batch is null)
        {
            return null;
        }

        // Loaded as they are; BatchRecordAssembler orders them and works out each
        // checkpoint's state.
        var events = await _db.TimelineEvents
            .AsNoTracking()
            .Where(e => e.BatchId == batchId)
            .ToListAsync(cancellationToken);

        var deviations = await _db.DeviationRecords
            .AsNoTracking()
            .Where(d => d.BatchId == batchId)
            .ToListAsync(cancellationToken);

        var snapshot = await _db.UpstreamSnapshots
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.BatchId == batchId, cancellationToken);

        // Superseded ones are left out: only the active determination sets the lab result.
        var determinations = await _db.LabDeterminations
            .AsNoTracking()
            .Where(d => d.BatchId == batchId && !d.IsSuperseded)
            .ToListAsync(cancellationToken);

        return BatchRecordAssembler.Assemble(batch, events, snapshot, deviations, determinations);
    }

    public async Task<IReadOnlyList<BatchSummaryDto>> FindAsync(
        string? dispatchRef,
        string? batchReference,
        CancellationToken cancellationToken = default)
    {
        var query = _db.BatchRecords.AsNoTracking();

        // Each filter given narrows the list; both together must both match.
        if (!string.IsNullOrWhiteSpace(dispatchRef))
        {
            var value = dispatchRef.Trim();
            query = query.Where(b => b.DispatchRef == value);
        }

        if (!string.IsNullOrWhiteSpace(batchReference))
        {
            var value = batchReference.Trim();
            query = query.Where(b => b.BatchReference == value);
        }

        return await query
            .OrderBy(b => b.FirstEventAt)
            .ThenBy(b => b.BatchId)
            .Select(ToSummary)
            .ToListAsync(cancellationToken);
    }

    // A batch_records row as a summary, for a query's Select. Shared with the batch search.
    internal static readonly Expression<Func<BatchRecord, BatchSummaryDto>> ToSummary = b => new BatchSummaryDto
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
        // The subquery runs in MySQL; LabResults.From maps its result as rows are read.
        LabResult = LabResults.From(b.LabDeterminations
            .Where(d => !d.IsSuperseded)
            .OrderByDescending(d => d.DeterminedAt)
            .Select(d => (DeterminationResult?)d.Result)
            .FirstOrDefault()),
    };
}

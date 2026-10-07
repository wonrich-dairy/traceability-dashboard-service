using Microsoft.EntityFrameworkCore;
using TraceabilityService.Api.Contracts;
using TraceabilityService.Api.Infrastructure.Persistence;

namespace TraceabilityService.Application;

public class TraceService(TraceabilityDbContext db) : ITraceService
{
    private readonly TraceabilityDbContext _db = db;

    public async Task<BatchTraceResponseDto?> GetBatchTraceAsync(
        string batchId,
        CancellationToken cancellationToken = default)
    {
        var batch = await _db.BatchTraces
            .AsNoTracking()
            // Ordered includes, so the checkpoints arrive in timestamp order rather
            // than being sorted after the fact. ThenBy(Id) keeps two checkpoints
            // sharing a timestamp in a stable order instead of whatever MySQL returns.
            .Include(t => t.Checkpoints.OrderBy(r => r.RecordedAtTimestamp).ThenBy(r => r.Id))
            .Include(t => t.Sources.OrderBy(s => s.ConsignmentReference))
            // Two collection includes in one query would multiply the rows together;
            // split sends one SELECT per collection and keeps the ordering above.
            .AsSplitQuery()
            .SingleOrDefaultAsync(t => t.BatchId == batchId, cancellationToken);

        if (batch is null)
        {
            return null;
        }

        return new BatchTraceResponseDto
        {
            BatchId = batch.BatchId,
            ProductionDate = batch.ProductionDate,
            CurrentStatus = batch.CurrentStatus,
            FinalLabOutcome = batch.FinalLabOutcome,

            Checkpoints = [.. batch.Checkpoints
                .Select(r => new CheckpointDto
                {
                    CheckpointName = r.CheckpointName,
                    RecordedAtTimestamp = r.RecordedAtTimestamp,
                    FatPercentage = r.FatPercentage,
                    SnfPercentage = r.SnfPercentage,
                    ClrReading = r.ClrReading,
                    TemperatureCelsius = r.TemperatureCelsius,
                    PH = r.PH,
                    Appearance = r.Appearance,
                    Texture = r.Texture,
                    Taste = r.Taste,
                    Colour = r.Colour,
                    Smell = r.Smell,
                })],

            SourceConsignments = [.. batch.Sources.Select(s => s.ConsignmentReference)],
        };
    }
}

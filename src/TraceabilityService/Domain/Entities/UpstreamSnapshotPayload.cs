using System.Text.Json;

namespace TraceabilityService.Domain.Entities;

/// <summary>
/// The JSON stored in <see cref="UpstreamSnapshot.Payload"/> once resolved: what the MCC
/// and Intake Service knows about a batch's dispatch. A contract: the resolver (SCRUM-132)
/// writes it with <see cref="Serialize"/> and the batch record endpoint reads it with
/// <see cref="Parse"/>. The shape is documented field by field in docs/schema.md; change
/// both together.
/// </summary>
public sealed class UpstreamSnapshotPayload
{
    // camelCase names, and a missing required member fails the read rather than coming
    // back as null.
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public required string BatchReference{get; set;}
    public required DispatchPart Dispatch{get; set;}
    public required IntakePart Intake{get; set;}
    public List<TankPart> Tanks{get; set;} = [];

    public static UpstreamSnapshotPayload Parse(string json) =>
        JsonSerializer.Deserialize<UpstreamSnapshotPayload>(json, JsonOptions)
        ?? throw new JsonException("Upstream snapshot payload is JSON null.");

    public static string Serialize(UpstreamSnapshotPayload payload) =>
        JsonSerializer.Serialize(payload, JsonOptions);

    public sealed class DispatchPart
    {
        public required string Reference{get; set;}

        // UTC. The MCC and Intake Service sends factory local time; convert before storing.
        public required DateTime DispatchedAt{get; set;}
        public string? DispatchedBy{get; set;}
        public string? BowserRegistration{get; set;}
        public required decimal TotalLitres{get; set;}
    }

    public sealed class IntakePart
    {
        // UTC, as above.
        public required DateTime ArrivedAt{get; set;}
        public required DateTime ScreenedAt{get; set;}
        public string? ScreenedBy{get; set;}
        public decimal? TemperatureC{get; set;}
    }

    public sealed class TankPart
    {
        public required string Code{get; set;}
        public required decimal QuantityDrawnLitres{get; set;}
        public List<ConsignmentPart> Consignments{get; set;} = [];
    }

    public sealed class ConsignmentPart
    {
        public required string Reference{get; set;}
        public required SocietyPart Society{get; set;}
        public required decimal VolumeLitres{get; set;}
        public required DateTime RegisteredAt{get; set;}
        public string? RegisteredBy{get; set;}

        // The gate quality test. Null if none was recorded.
        public PanelPart? Panel{get; set;}
    }

    public sealed class SocietyPart
    {
        public required string Code{get; set;}
        public string? Name{get; set;}
    }

    public sealed class PanelPart
    {
        public required decimal FatPercentage{get; set;}
        public required decimal SnfPercentage{get; set;}
        public required decimal ClrReading{get; set;}
        public required decimal TemperatureC{get; set;}
        public required string Verdict{get; set;}

        // The MCC and Intake Service's margin scoring: how close the closest measure came to
        // the limit that would have rejected it, as a fraction of that limit (0 = on it),
        // and which measure that was, e.g. Snf. Null when it could not be scored; the
        // service's -1 placeholder for that is not copied. The marginal societies panel
        // ranks on these.
        public decimal? TightestMargin{get; set;}
        public string? TightestMeasure{get; set;}
    }
}

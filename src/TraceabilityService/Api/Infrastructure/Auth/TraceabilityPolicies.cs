namespace TraceabilityService.Api.Infrastructure;

/// <summary>
/// Authorization policy names, for [Authorize(Policy = ...)]. The roles behind each are
/// set in <see cref="TraceabilityAuthExtensions.AddTraceabilityAuthorization"/>.
/// </summary>
public static class TraceabilityPolicies
{
    /// <summary>
    /// Read batch records and the QC dashboard (SCRUM-133, SCRUM-135): Quality Control
    /// Officers and System Administrators. Any other role is refused with 403.
    /// </summary>
    public const string QualityControl = "QualityControl";
}

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

    /// <summary>
    /// Read the consolidated audit trail and its coverage list (SCRUM-138): System Administrators
    /// and Quality Control Officers. Its own policy, though the roles are the same today, so
    /// who may read the audit trail can change without widening batch record access.
    /// </summary>
    public const string AuditTrail = "AuditTrail";
}

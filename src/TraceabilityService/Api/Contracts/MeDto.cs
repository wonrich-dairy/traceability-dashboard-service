namespace TraceabilityService.Api.Contracts;

/// <summary>
/// GET /api/me: the caller as their token describes them. A claim the token lacks is null.
/// </summary>
public class MeDto
{
    /// <summary>The Auth Service user ID.</summary>
    public string? UserId{get; set;}

    /// <summary>The user name the caller signed in with.</summary>
    public string? UserName{get; set;}

    /// <summary>The caller's role, e.g. QualityControlOfficer.</summary>
    public string? Role{get; set;}

    /// <summary>The facility the caller belongs to, e.g. FACTORY-01.</summary>
    public string? Facility{get; set;}
}

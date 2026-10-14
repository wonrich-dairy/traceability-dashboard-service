using Microsoft.AspNetCore.Mvc;
using TraceabilityService.Api.Contracts;
using Wonrich.Auth.Tokens;

namespace TraceabilityService.Api.Controllers;

/// <summary>
/// GET /api/me returns the caller's identity from the JWT (SCRUM-119).
/// Proves the auth ACs before SCRUM-133 adds real protected endpoints: no token -> 401, valid token ->
/// user id, role and facility available to every endpoint via the shared WonrichPrincipalExtensions.
/// No [Authorize] on purpose: the fallback policy in TraceabilityAuthExtensions is what protects it.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class MeController : ControllerBase
{
    /// <summary>The caller's identity, as read from their token. Any role.</summary>
    /// <response code="200">The caller.</response>
    [HttpGet]
    [ProducesResponseType(typeof(MeDto), StatusCodes.Status200OK, "application/json")]
    public ActionResult<MeDto> Get()
    {
        return Ok(new MeDto
        {
            UserId = User.UserId(),
            UserName = User.UserName(),
            Role = User.Role(),
            Facility = User.Facility()
        });
    }
}

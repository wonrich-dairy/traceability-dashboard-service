using Microsoft.AspNetCore.Mvc;
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
    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new
        {
            userId = User.UserId(),
            userName = User.UserName(),
            role = User.Role(),
            facility = User.Facility()
        });
    }
}

using Microsoft.AspNetCore.Mvc;
using TraceabilityService.Application;
using TraceabilityService.Observability;
using Wonrich.Auth.Tokens;

namespace TraceabilityService.Api.Infrastructure;

public static class AuditRequesterExtensions
{
    // The caller, as an audit entry names them. Only called once the policy has authenticated
    // the caller, so the token names them.
    public static AuditRequester AuditRequester(this ControllerBase controller) => new(
        controller.User.UserId() ?? controller.User.UserName() ?? "unknown",
        controller.User.Role(),
        controller.HttpContext.Items[CorrelationIdMiddleware.HeaderName] as string);
}

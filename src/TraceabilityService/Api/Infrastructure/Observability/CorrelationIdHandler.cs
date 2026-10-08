namespace TraceabilityService.Api.Infrastructure.Observability;

/// <summary>
/// DelegatingHandler that propagates X-Correlation-ID to downstream HTTP calls, copied from Processing Service (SCRUM-90).
/// Used when Traceability calls other services - correlation traced across services.
/// </summary>
public sealed class CorrelationIdHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CorrelationIdHandler(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context != null && context.Items.TryGetValue(CorrelationIdMiddleware.HeaderName, out var correlationId) && correlationId is string id)
        {
            request.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, id);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}

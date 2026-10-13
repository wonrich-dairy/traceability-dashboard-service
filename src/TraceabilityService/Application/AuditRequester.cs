namespace TraceabilityService.Application;

/// <summary>Who asked for something Traceability audits (an export, an opened trace), for its audit entry.</summary>
public sealed record AuditRequester(string UserId, string? Role, string? CorrelationId);

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TraceabilityService.Observability;

namespace TraceabilityService.Infrastructure.MccIntake;

/// <summary>
/// The two MCC and Intake Service lookups that resolve a batch's upstream snapshot (SCRUM-132):
/// factory intake's batch by dispatch note, which gives the batch reference, then the trace of
/// that batch back to its tanks and consignments (SCRUM-12).
/// </summary>
public interface IMccIntakeClient
{
    /// <summary>The batches factory intake created from one dispatch note, newest first. Empty if none.</summary>
    /// <exception cref="HttpRequestException">Unreachable, or any answer but 200.</exception>
    Task<IReadOnlyList<BatchView>> FindBatchesAsync(string dispatchNoteReference, string correlationId, CancellationToken cancellationToken);

    /// <summary>The batch traced back to its source, or null when no batch carries that reference.</summary>
    /// <exception cref="HttpRequestException">Unreachable, or any answer but 200 or that 404.</exception>
    Task<BatchTraceView?> TraceBatchAsync(string batchReference, string correlationId, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IMccIntakeClient"/> on a typed HttpClient: base address and timeout from
/// Services:MccIntake, the service token from <see cref="ServiceTokenHandler"/>, and the caller's
/// correlation ID on every request. A timeout surfaces as a <see cref="TaskCanceledException"/>.
/// </summary>
public sealed class MccIntakeClient(HttpClient http) : IMccIntakeClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<BatchView>> FindBatchesAsync(
        string dispatchNoteReference, string correlationId, CancellationToken cancellationToken) =>
        await GetAsync<List<BatchView>>(
            $"api/factory/batches?dispatchNote={Uri.EscapeDataString(dispatchNoteReference)}",
            correlationId, notFoundIsNull: false, cancellationToken) ?? [];

    public Task<BatchTraceView?> TraceBatchAsync(string batchReference, string correlationId, CancellationToken cancellationToken) =>
        GetAsync<BatchTraceView>(
            $"api/factory/batches/{Uri.EscapeDataString(batchReference)}/trace",
            correlationId, notFoundIsNull: true, cancellationToken);

    private async Task<T?> GetAsync<T>(string path, string correlationId, bool notFoundIsNull, CancellationToken cancellationToken)
        where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, correlationId);

        using var response = await http.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound && notFoundIsNull && await IsEntityNotFoundAsync(response, cancellationToken))
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"MCC and Intake Service answered {(int)response.StatusCode} {response.ReasonPhrase} to GET {path}",
                null,
                response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
            ?? throw new JsonException($"MCC and Intake Service returned JSON null for GET {path}.");
    }

    // Only the service's own "no such batch" counts: a 404 without that code is a wrong base URL
    // or route, which a retry after the configuration is fixed would get past.
    private static async Task<bool> IsEntityNotFoundAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<IntakeProblemDetails>(Json, cancellationToken);
            return problem?.Code == IntakeProblemDetails.EntityNotFound;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return false;
        }
    }
}

public static class MccIntakeServiceCollectionExtensions
{
    /// <summary>
    /// The MCC and Intake Service client, its token provider, and the Auth Service client the
    /// provider signs in with. Registered even when Services:MccIntake:BaseUrl is empty, so the
    /// app starts without it; the retry worker then stays idle.
    /// </summary>
    public static IServiceCollection AddMccIntakeClient(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MccIntakeOptions>().Bind(configuration.GetSection(MccIntakeOptions.Section));
        services.AddOptions<AuthServiceOptions>().Bind(configuration.GetSection(AuthServiceOptions.Section));

        services.AddHttpClient(ServiceTokenProvider.HttpClientName, (provider, http) =>
        {
            Configure(http, provider.GetRequiredService<IOptions<AuthServiceOptions>>().Value.BaseUrl,
                provider.GetRequiredService<IOptions<MccIntakeOptions>>().Value.Timeout);
        });
        services.AddSingleton<IServiceTokenProvider, ServiceTokenProvider>();

        services.AddTransient<ServiceTokenHandler>();
        services.AddHttpClient<IMccIntakeClient, MccIntakeClient>((provider, http) =>
            {
                var options = provider.GetRequiredService<IOptions<MccIntakeOptions>>().Value;
                Configure(http, options.BaseUrl, options.Timeout);
            })
            .AddHttpMessageHandler<ServiceTokenHandler>();

        return services;
    }

    private static void Configure(HttpClient http, string baseUrl, TimeSpan timeout)
    {
        // A trailing slash, so relative paths append to the base path instead of replacing its last segment.
        if (Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseAddress))
        {
            http.BaseAddress = baseAddress;
        }

        http.Timeout = timeout;
    }
}

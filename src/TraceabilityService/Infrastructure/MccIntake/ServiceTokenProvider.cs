using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace TraceabilityService.Infrastructure.MccIntake;

/// <summary>
/// The bearer token this service presents to other Wonrich services. The Auth Service has no
/// client-credentials grant, so service-to-service means signing in as a service account
/// (Services:MccIntake:ClientId and ClientSecret) like any user does.
/// </summary>
public interface IServiceTokenProvider
{
    /// <summary>A token with at least <see cref="ServiceTokenProvider.ExpiryMargin"/> left, fetched if need be.</summary>
    /// <exception cref="HttpRequestException">The Auth Service refused the sign-in or could not be reached.</exception>
    Task<string> GetTokenAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Forgets the token if it is still the cached one, so the next call signs in again. For a 401:
    /// the token was revoked or the signing key rotated before it expired.
    /// </summary>
    void Invalidate(string token);
}

/// <summary>
/// <see cref="IServiceTokenProvider"/> that caches one token until a minute before it expires.
/// A singleton, so every caller shares the token; concurrent callers wait for one sign-in
/// rather than each starting their own.
/// </summary>
public sealed class ServiceTokenProvider(
    IHttpClientFactory httpClients,
    IOptions<MccIntakeOptions> options,
    TimeProvider time,
    ILogger<ServiceTokenProvider> logger) : IServiceTokenProvider, IDisposable
{
    public const string HttpClientName = "AuthService";

    // Renewed this long before it expires, so a token never runs out mid-request.
    public static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _signIn = new(1, 1);
    // Read without the lock: a stale read only sends the caller through the sign-in check.
    private CachedToken? _cached;

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (Usable(_cached) is { } token)
        {
            return token;
        }

        await _signIn.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have signed in while this one waited.
            if (Usable(_cached) is { } fetchedMeanwhile)
            {
                return fetchedMeanwhile;
            }

            var fetched = await SignInAsync(cancellationToken);
            _cached = fetched;
            return fetched.AccessToken;
        }
        finally
        {
            _signIn.Release();
        }
    }

    public void Invalidate(string token)
    {
        var cached = _cached;
        if (cached?.AccessToken == token)
        {
            Interlocked.CompareExchange(ref _cached, null, cached);
        }
    }

    public void Dispose() => _signIn.Dispose();

    private string? Usable(CachedToken? cached) =>
        cached is not null && time.GetUtcNow().UtcDateTime < cached.RenewAt ? cached.AccessToken : null;

    private async Task<CachedToken> SignInAsync(CancellationToken cancellationToken)
    {
        var account = options.Value;
        var http = httpClients.CreateClient(HttpClientName);

        using var response = await http.PostAsJsonAsync(
            "api/auth/login", new LoginRequest(account.ClientId, account.ClientSecret), Json, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // The body says nothing more: the Auth Service gives every refusal the same shape.
            throw new HttpRequestException(
                $"Auth Service refused the service account sign-in: {(int)response.StatusCode} {response.ReasonPhrase}",
                null,
                response.StatusCode);
        }

        var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>(Json, cancellationToken);
        if (tokens is null || string.IsNullOrEmpty(tokens.AccessToken))
        {
            throw new HttpRequestException("Auth Service sign-in returned no access token.");
        }

        var expiresAt = DateTime.SpecifyKind(tokens.ExpiresAtUtc, DateTimeKind.Utc);
        logger.LogInformation("Signed in to the Auth Service as {ServiceAccount}; token expires at {ExpiresAt:o}",
            account.ClientId, expiresAt);

        return new CachedToken(tokens.AccessToken, expiresAt - ExpiryMargin);
    }

    private sealed record CachedToken(string AccessToken, DateTime RenewAt);

    // The Auth Service's sign-in contract (WD-Auth-service, SRC/Controllers/AuthController.cs at
    // commit cbf37b2c8ce1201a84ad91dfa1d9ffdffafb0d99): LoginRequest, and the members of
    // TokenResponse this service reads.
    private sealed record LoginRequest(string UserName, string Password);

    private sealed record TokenResponse(string AccessToken, DateTime ExpiresAtUtc);
}

/// <summary>
/// Puts the service token on every request to the MCC and Intake Service. On a 401 it drops the
/// token, signs in again and sends the request once more: a token can be revoked, or the
/// signing key rotated, before it expires. A second 401 is returned as it is.
/// </summary>
public sealed class ServiceTokenHandler(IServiceTokenProvider tokens) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await tokens.GetTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized)
        {
            return response;
        }

        // Resending is safe: the client only sends GETs, which have no body to have been consumed.
        response.Dispose();
        tokens.Invalidate(token);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(cancellationToken));
        return await base.SendAsync(request, cancellationToken);
    }
}

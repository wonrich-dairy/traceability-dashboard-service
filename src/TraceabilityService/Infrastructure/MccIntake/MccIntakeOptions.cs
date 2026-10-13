namespace TraceabilityService.Infrastructure.MccIntake;

/// <summary>
/// Services:MccIntake: where the MCC and Intake Service is, and the service account this service
/// signs in to the Auth Service as to call it. The account needs a role that grants the MCC and
/// Intake Service's TraceBatches and ScreenFactoryArrivals policies: the trace and the batch
/// lookup by dispatch note answer to those.
/// </summary>
public sealed class MccIntakeOptions
{
    public const string Section = "Services:MccIntake";

    public string BaseUrl { get; set; } = "";

    // Per request, to the MCC and Intake Service and to the Auth Service alike.
    public int TimeoutSeconds { get; set; } = 10;

    // The service account's user name and password at the Auth Service.
    public string ClientId { get; set; } = "";

    public string ClientSecret { get; set; } = "";

    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds > 0 ? TimeoutSeconds : 10);
}

/// <summary>Services:Auth: where the Auth Service is, for the service account's sign-in.</summary>
public sealed class AuthServiceOptions
{
    public const string Section = "Services:Auth";

    public string BaseUrl { get; set; } = "";
}

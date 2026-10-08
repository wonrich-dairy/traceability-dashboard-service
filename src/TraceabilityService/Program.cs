using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Prometheus;
using TraceabilityService.Api.Infrastructure.Persistence;
using TraceabilityService.Health;
using TraceabilityService.Observability;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// ── Database (MySQL) ─────────────────────────────────────────────────────────
var connectionString = builder.Configuration.GetConnectionString("TraceabilityDb");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "ConnectionStrings:TraceabilityDb is not configured. "
        + "Set it in .env (Docker), user secrets (dotnet run) or App Service settings (Azure).");
}

// Pinned rather than AutoDetect so startup doesn't need a reachable server and
// every environment gets the same SQL. Keep in step with the deployed MySQL.
builder.Services.AddDbContext<TraceabilityDbContext>(options =>
    options.UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 46))));

// ── CORS: the frontend's origins come from configuration (Cors__AllowedOrigins__0, ...) ──
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                     ?? ["http://localhost:5173", "http://127.0.0.1:5173"];
builder.Services.AddCors(options => options.AddPolicy("TraceabilityCors", policy =>
    policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

// ── Health ───────────────────────────────────────────────────────────────────
// mysql failing = Unhealthy (deploy fails); Kafka failing = Degraded (deploy still passes).
builder.Services.AddHealthChecks()
    .AddDbContextCheck<TraceabilityDbContext>("mysql")
    .AddCheck<KafkaHealthCheck>("kafka", failureStatus: HealthStatus.Degraded);

var app = builder.Build();

// Observability first: every later log line carries the correlation ID, and every request is measured.
app.UseCorrelationId();
app.UseRequestMetrics();

// Apply migrations on startup in Development and Staging. Production applies the migration
// script from the pipeline before the deploy instead.
if (app.Environment.IsDevelopment() || app.Environment.IsStaging())
{
    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();
        db.Database.Migrate();
    }
    catch (Exception ex)
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogWarning(ex, "Auto-migrate skipped: database not reachable at startup.");
    }
}

app.UseCors("TraceabilityCors");

// Health is anonymous (container probes and the post-deploy check).
// Shape { status, checks: [ { name, status, description } ] } is what scripts/verify-health.sh
// and the integration tests read, same as Quality Lab: keep it.
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description
            })
        }));
    }
});

// Prometheus scrape endpoint, anonymous so Prometheus needs no token.
app.MapMetrics();

// Deployed commit, baked into the image as GIT_SHA. The pipeline waits for this to show the new SHA.
app.MapGet("/version", () => Results.Ok(new
{
    sha = Environment.GetEnvironmentVariable("GIT_SHA") ?? "local"
}));

// Root descriptor
app.MapGet("/", (IWebHostEnvironment env) => Results.Ok(new
{
    service = "Wonrich Traceability Service",
    environment = env.EnvironmentName,
    health = "/health",
    version = "/version",
    metrics = "/metrics",
}));

app.MapControllers();

app.Run();

public partial class Program;

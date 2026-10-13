using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Prometheus;
using TraceabilityService.Api.Controllers;
using TraceabilityService.Api.Infrastructure;
using TraceabilityService.Api.Infrastructure.Persistence;
using TraceabilityService.Application;
using TraceabilityService.Health;
using TraceabilityService.Infrastructure.Kafka;
using TraceabilityService.Observability;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// ── Swagger (SCRUM-125, shared template from SCRUM-49): /swagger, off in Production ──
builder.Services.AddTraceabilitySwagger();

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
// TriggerAwareMySqlMigrator keeps the idempotent migration script runnable with the
// audit_entries triggers in it (SCRUM-138).
builder.Services.AddDbContext<TraceabilityDbContext>(options =>
    options.UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 46)))
        .ReplaceService<IModelCacheKeyFactory, TraceabilityModelCacheKeyFactory>()
        .ReplaceService<IMigrator, TriggerAwareMySqlMigrator>());

// ── Read model queries ───────────────────────────────────────────────────────
builder.Services.AddScoped<IBatchRecordService, BatchRecordService>();
builder.Services.AddScoped<IBatchTraceService, BatchTraceService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<IBatchSearchService, BatchSearchService>();
builder.Services.AddScoped<IAuditTrailService, AuditTrailService>();
builder.Services.AddScoped<IDeadLetterService, KafkaDeadLetterService>();
builder.Services.AddScoped<IProjectionRebuildService, ProjectionRebuildService>();

// ── Kafka consumers ──────────────────────────────────────────────────────────
// Each one: a transaction per event with a duplicate check, retries, then dead-letters, and
// commits after either (EventProcessor). Handlers are per event type.
builder.Services.AddLabDeterminationConsumer(builder.Configuration);
builder.Services.AddProcessingConsumers(builder.Configuration);

// ── Upstream snapshots (MCC and Intake Service) ──────────────────────────────
// A new batch's snapshot is tried as soon as its event commits, then retried in the background
// with backoff (Services:MccIntake, Services:Auth, Upstream). Idle until those are configured.
builder.Services.AddUpstreamResolution(builder.Configuration);

// "Today" for the dashboard is the date in Asia/Colombo.
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton<FactoryCalendar>();

// ── Authentication and authorization (Auth Service tokens; deny anonymous by default) ──
builder.Services.AddTraceabilityAuthentication(builder.Configuration);
builder.Services.AddTraceabilityAuthorization();

// ── CORS: the frontend's origins come from configuration (Cors__AllowedOrigins__0, ...) ──
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                     ?? ["http://localhost:5173", "http://127.0.0.1:5173"];
// The export's file name and row count are in headers, which a browser hides cross-origin unless exposed.
builder.Services.AddCors(options => options.AddPolicy("TraceabilityCors", policy =>
    policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()
        .WithExposedHeaders("Content-Disposition", BatchSearchController.BatchCountHeader)));

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
        // Unreachable, or a migration failed: e.g. creating the audit_entries triggers without
        // log_bin_trust_function_creators (docs/database.md). The exception says which.
        logger.LogWarning(ex, "Auto-migrate failed: the database is unreachable or a migration could not be applied.");
    }
}

// Development and Staging only; ahead of authorization so the page and spec load without a token.
app.UseTraceabilitySwagger();

app.UseCors("TraceabilityCors");
app.UseAuthentication();
app.UseAuthorization();

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
}).AllowAnonymous();

// Prometheus scrape endpoint, anonymous so Prometheus needs no token.
app.MapMetrics().AllowAnonymous();

// Deployed commit, baked into the image as GIT_SHA. The pipeline waits for this to show the new SHA.
// Operational, like /health and /metrics, so kept out of the API's Swagger document.
app.MapGet("/version", () => Results.Ok(new
{
    sha = Environment.GetEnvironmentVariable("GIT_SHA") ?? "local"
})).AllowAnonymous().ExcludeFromDescription();

// Root descriptor. JSON rather than a redirect to /swagger, because Swagger is off in Production.
app.MapGet("/", (IWebHostEnvironment env) => Results.Ok(new
{
    service = "Wonrich Traceability Service",
    environment = env.EnvironmentName,
    health = "/health",
    version = "/version",
    metrics = "/metrics",
    swagger = env.IsProduction() ? null : "/swagger",
})).AllowAnonymous().ExcludeFromDescription();

app.MapControllers();

app.Run();

// Exposes Program to WebApplicationFactory<Program> in TraceabilityService.IntegrationTests
public partial class Program;
using System.Text.Json;
using System.Text.Json.Serialization;
using TraceabilityService.Api.Infrastructure;
using TraceabilityService.Api.Infrastructure.Observability;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddOpenApi();

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        "Connection string 'Default' not found. Set it with: " +
        "dotnet user-secrets set \"ConnectionStrings:Default\" \"<connection string>\"");

// Authentication and authorization (shared Auth Service tokens, 401 for unauthenticated except /health)
builder.Services.AddTraceabilityAuthentication(builder.Configuration);
builder.Services.AddTraceabilityAuthorization();
builder.Services.AddTraceabilityCors(builder.Configuration);

// Observability (metrics, structured logging, correlation ID)
builder.Services.AddTraceabilityObservability(builder.Configuration);

builder.Services.AddHealthChecks();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    // Dev only: Swagger UI fetches the spec without a token
    app.MapOpenApi().AllowAnonymous();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "Traceability Service v1"));
}

app.UseTraceabilityObservability();

app.UseHttpsRedirection();

app.UseTraceabilityCors();
app.UseAuthentication();
app.UseAuthorization();

// Health is anonymous (container runtime probes before token)
app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => new { status = entry.Value.Status.ToString(), description = entry.Value.Description }),
        }));
    }
}).AllowAnonymous();

// Metrics is anonymous for Prometheus scraping
app.MapTraceabilityMetrics();

app.MapControllers();

app.Run();



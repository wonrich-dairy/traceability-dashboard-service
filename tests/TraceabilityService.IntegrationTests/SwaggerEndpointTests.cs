using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace TraceabilityService.IntegrationTests;

/// <summary>
/// The service's own Swagger (SCRUM-125): UI and spec on /swagger without a token, every
/// controller endpoint in the spec with its schemas, auth and XML comments, and none of it in
/// Production.
/// </summary>
[Collection(ApiCollection.Name)]
public class SwaggerEndpointTests(TraceabilityApiFactory factory)
{
    private const string SpecPath = "/swagger/v1/swagger.json";

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    public async Task Ui_and_spec_are_served_without_a_token_outside_production(string environment)
    {
        using var app = factory.WithWebHostBuilder(b => b.UseEnvironment(environment));
        var client = app.CreateClient();

        var ui = await client.GetAsync("/swagger/index.html");
        var spec = await client.GetAsync(SpecPath);

        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
        Assert.Equal("text/html", ui.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, spec.StatusCode);
    }

    [Fact]
    public async Task Ui_and_spec_are_not_served_in_production()
    {
        using var app = factory.WithWebHostBuilder(b => b.UseEnvironment("Production"));
        var client = app.CreateClient();

        var ui = await client.GetAsync("/swagger/index.html");
        var spec = await client.GetAsync(SpecPath);
        var root = await client.GetAsync("/");

        // 401, not 404: with nothing serving them they fall to the deny-by-default policy.
        Assert.Equal(HttpStatusCode.Unauthorized, ui.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, spec.StatusCode);
        using var descriptor = JsonDocument.Parse(await root.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, descriptor.RootElement.GetProperty("swagger").ValueKind);
    }

    // The definition of done: a new controller endpoint shows up with no Swagger configuration.
    [Fact]
    public async Task Every_controller_endpoint_is_in_the_spec()
    {
        using var spec = await GetSpec();
        var paths = spec.RootElement.GetProperty("paths");

        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<ControllerActionDescriptor>() is not null)
            .SelectMany(e => e.Metadata.GetRequiredMetadata<HttpMethodMetadata>().HttpMethods
                .Select(method => (Path: "/" + e.RoutePattern.RawText!.TrimStart('/'), Method: method.ToLowerInvariant())))
            .ToList();

        Assert.NotEmpty(endpoints);
        Assert.All(endpoints, endpoint =>
        {
            Assert.True(paths.TryGetProperty(endpoint.Path, out var path), $"{endpoint.Path} is not in the spec");
            Assert.True(path.TryGetProperty(endpoint.Method, out _), $"{endpoint.Method} {endpoint.Path} is not in the spec");
        });
    }

    [Fact]
    public async Task Operations_carry_their_parameters_and_request_and_response_schemas()
    {
        using var spec = await GetSpec();
        var paths = spec.RootElement.GetProperty("paths");

        var getBatch = paths.GetProperty("/api/batches/{batchId}").GetProperty("get");
        Assert.Equal("#/components/schemas/BatchRecordDto", ResponseSchemaRef(getBatch, "200"));
        Assert.Equal("#/components/schemas/ProblemDetails", ResponseSchemaRef(getBatch, "404", "application/problem+json"));
        Assert.Contains(getBatch.GetProperty("parameters").EnumerateArray(),
            p => p.GetProperty("name").GetString() == "batchId" && p.GetProperty("in").GetString() == "path");

        // The dashboard's query object is spread into its query string parameters.
        var summary = paths.GetProperty("/api/dashboard/summary").GetProperty("get");
        var query = summary.GetProperty("parameters").EnumerateArray()
            .Select(p => p.GetProperty("name").GetString()).ToList();
        Assert.Equal(["facility", "productLine", "from", "to"], query);
        Assert.Equal("#/components/schemas/DashboardSummaryDto", ResponseSchemaRef(summary, "200"));

        var schemas = spec.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.GetProperty("BatchRecordDto").GetProperty("properties").TryGetProperty("checkpoints", out _));
        Assert.True(schemas.TryGetProperty("MeDto", out _));
        // Enums travel as their names, as JsonStringEnumConverter writes them.
        Assert.Contains(schemas.GetProperty("BatchStatus").GetProperty("enum").EnumerateArray(),
            v => v.ValueKind == JsonValueKind.String);
    }

    [Fact]
    public async Task Bearer_authentication_is_declared_and_required_with_the_roles()
    {
        using var spec = await GetSpec();

        var scheme = spec.RootElement.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer");
        Assert.Equal("http", scheme.GetProperty("type").GetString());
        Assert.Equal("bearer", scheme.GetProperty("scheme").GetString());

        // Deny-by-default: every operation needs the token and documents 401.
        foreach (var (path, method, operation) in Operations(spec))
        {
            var security = operation.GetProperty("security").EnumerateArray().ToList();
            Assert.Contains(security, s => s.TryGetProperty("Bearer", out _));
            Assert.True(operation.GetProperty("responses").TryGetProperty("401", out _), $"{method} {path} has no 401");
        }

        // QualityControl policy: its roles are named, and 403 documented. /api/me takes any role.
        var trace = spec.RootElement.GetProperty("paths").GetProperty("/api/batches/{batchId}/trace").GetProperty("get");
        Assert.Contains("QualityControlOfficer, SystemAdministrator", trace.GetProperty("description").GetString());
        Assert.True(trace.GetProperty("responses").TryGetProperty("403", out _));

        // api/[controller]: the spec keeps the class name's case
        var me = spec.RootElement.GetProperty("paths").GetProperty("/api/Me").GetProperty("get");
        Assert.False(me.GetProperty("responses").TryGetProperty("403", out _));
    }

    [Fact]
    public async Task Xml_documentation_comments_are_rendered()
    {
        using var spec = await GetSpec();
        var root = spec.RootElement;

        var getBatch = root.GetProperty("paths").GetProperty("/api/batches/{batchId}").GetProperty("get");
        Assert.StartsWith("The full record for one batch", getBatch.GetProperty("summary").GetString());
        Assert.Equal("No batch carries that ID.", getBatch.GetProperty("responses").GetProperty("404").GetProperty("description").GetString());
        var batchId = getBatch.GetProperty("parameters").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "batchId");
        Assert.Contains("262-FM-B", batchId.GetProperty("description").GetString());

        var trace = root.GetProperty("paths").GetProperty("/api/batches/{batchId}/trace").GetProperty("get");
        Assert.Contains("earliest checkpoint in stage order", trace.GetProperty("description").GetString());

        var labResult = root.GetProperty("components").GetProperty("schemas")
            .GetProperty("BatchSummaryDto").GetProperty("properties").GetProperty("labResult");
        Assert.Contains("Pending until Quality Lab", labResult.GetProperty("description").GetString());
    }

    private async Task<JsonDocument> GetSpec()
    {
        var response = await factory.CreateClient().GetAsync(SpecPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static IEnumerable<(string Path, string Method, JsonElement Operation)> Operations(JsonDocument spec) =>
        spec.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject().Select(op => (path.Name, op.Name, op.Value)));

    private static string? ResponseSchemaRef(JsonElement operation, string status, string mediaType = "application/json") =>
        operation.GetProperty("responses").GetProperty(status).GetProperty("content")
            .GetProperty(mediaType).GetProperty("schema").GetProperty("$ref").GetString();
}

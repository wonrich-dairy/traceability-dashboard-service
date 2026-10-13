using System.Text;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Testcontainers.MySql;

namespace TraceabilityService.IntegrationTests;

/// <summary>
/// Starts a throwaway MySQL container and runs the real API against it.
/// EF Core migrations are applied by the app on startup (Development), so every
/// integration test also proves the migrations run cleanly against an empty database.
/// </summary>
public class TraceabilityApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string SigningKey = "traceability-tests-signing-key-not-used-anywhere-else";
    public const string Issuer = "wonrich-auth";
    public const string Audience = "wonrich-services";

    private readonly MySqlContainer _mysql = new MySqlBuilder("mysql:8.0")
        .WithDatabase("traceability")
        .WithUsername("trc_app")
        .WithPassword("trc_test_pwd")
        .Build();

    public async Task InitializeAsync() => await _mysql.StartAsync();

    /// <summary>
    /// Runs a SQL script with the mysql command-line client inside the container, as root, against
    /// <paramref name="database"/> (created if missing): the way the pipeline applies the migration
    /// script to production. The app's user can only reach its own database, hence root.
    /// </summary>
    public async Task<ExecResult> RunScriptAsync(string database, string script)
    {
        var path = $"/tmp/{Guid.NewGuid():N}.sql";
        await _mysql.CopyAsync(Encoding.UTF8.GetBytes(script), path);
        return await _mysql.ExecAsync(
        [
            "sh", "-c",
            $"mysql -uroot -p\"$MYSQL_ROOT_PASSWORD\" -e 'CREATE DATABASE IF NOT EXISTS `{database}`' "
            + $"&& mysql -uroot -p\"$MYSQL_ROOT_PASSWORD\" {database} < {path}",
        ]);
    }

    /// <summary>Runs one statement as root against <paramref name="database"/>, for checking what a script left.</summary>
    public Task<ExecResult> QueryAsync(string database, string sql) =>
        _mysql.ExecAsync(["sh", "-c", $"mysql -uroot -p\"$MYSQL_ROOT_PASSWORD\" -N -B {database} -e \"{sql}\""]);

    async Task IAsyncLifetime.DisposeAsync() => await _mysql.DisposeAsync().AsTask();

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(config =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TraceabilityDb"] = _mysql.GetConnectionString(),
                // No broker in CI: the Kafka check reports Degraded, which is expected.
                ["Kafka:BootstrapServers"] = "localhost:1",
                // Nor should the consumers try to read from it; the handlers are tested directly.
                ["Kafka:Consumers:Enabled"] = "false",
                ["Auth:SigningKey"] = SigningKey,
                ["Auth:Issuer"] = Issuer,
                ["Auth:Audience"] = Audience,
            }));

        return base.CreateHost(builder);
    }
}

/// <summary>One MySQL container shared by every test class in the collection.</summary>
[CollectionDefinition(Name)]
public class ApiCollection : ICollectionFixture<TraceabilityApiFactory>
{
    public const string Name = "api";
}

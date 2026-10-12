using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Testcontainers.Kafka;
using Testcontainers.MySql;

namespace TraceabilityService.IntegrationTests;

/// <summary>
/// The real app against a real broker and MySQL, with its consumers running (SCRUM-130). Its own
/// containers, so the consumers never touch the shared <see cref="TraceabilityApiFactory"/>
/// database. The topics are created before the app starts, as wonrich-infra creates them: the
/// brokers in every environment have auto-creation off.
/// </summary>
public class KafkaApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public static readonly (string Name, int Partitions)[] Topics =
    [
        ("wonrich.processing.stage-events.v1", 3),
        ("wonrich.processing.hold-events.v1", 3),
        ("wonrich.quality-lab.batch-determinations.v1", 3),
        ("wonrich.dlq.traceability-stage-events.v1", 1),
        ("wonrich.dlq.traceability-hold-events.v1", 1),
        ("wonrich.dlq.traceability-batch-determinations.v1", 1),
    ];

    private readonly MySqlContainer _mysql = new MySqlBuilder("mysql:8.0")
        .WithDatabase("traceability")
        .WithUsername("trc_app")
        .WithPassword("trc_test_pwd")
        .Build();

    private readonly KafkaContainer _kafka = new KafkaBuilder("confluentinc/cp-kafka:7.6.1").Build();

    public string BootstrapServers { get; private set; } = "";

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_mysql.StartAsync(), _kafka.StartAsync());
        BootstrapServers = _kafka.GetBootstrapAddress().Replace("PLAINTEXT://", "", StringComparison.Ordinal);

        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = BootstrapServers }).Build();
        await admin.CreateTopicsAsync(Topics.Select(t => new TopicSpecification
        {
            Name = t.Name, NumPartitions = t.Partitions, ReplicationFactor = 1,
        }));

        // Start the app, and with it the consumers, once the topics exist.
        _ = Services;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        await Task.WhenAll(_mysql.DisposeAsync().AsTask(), _kafka.DisposeAsync().AsTask());
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(config =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TraceabilityDb"] = _mysql.GetConnectionString(),
                ["Kafka:BootstrapServers"] = BootstrapServers,
                ["Kafka:SecurityProtocol"] = "Plaintext",
                ["Kafka:Consumers:Enabled"] = "true",
                ["Auth:SigningKey"] = TraceabilityApiFactory.SigningKey,
                ["Auth:Issuer"] = TraceabilityApiFactory.Issuer,
                ["Auth:Audience"] = TraceabilityApiFactory.Audience,
            }));

        return base.CreateHost(builder);
    }
}

[CollectionDefinition(Name)]
public class KafkaCollection : ICollectionFixture<KafkaApiFactory>
{
    public const string Name = "kafka";
}

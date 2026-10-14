using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using SRC.Authorization;

namespace TraceabilityService.IntegrationTests;

[Collection(KafkaCollection.Name)]
public sealed class DeadLettersEndpointTests(KafkaApiFactory factory)
{
    private const string MissingTopic = "wonrich.dlq.traceability-stage-events.v1";
    private const string ExistingTopic = "wonrich.dlq.traceability-batch-determinations.v1";

    [Fact]
    public async Task Listing_all_groups_skips_missing_topic_and_group_endpoint_returns_not_found()
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig
        {
            BootstrapServers = factory.BootstrapServers,
        }).Build();
        await admin.DeleteTopicsAsync([MissingTopic]);

        try
        {
            var eventId = Guid.NewGuid().ToString();
            using (var producer = new ProducerBuilder<string, string>(new ProducerConfig
            {
                BootstrapServers = factory.BootstrapServers,
            }).Build())
            {
                await producer.ProduceAsync(ExistingTopic, new Message<string, string>
                {
                    Key = "dead-letter-test",
                    Value = """{"eventId":"test"}""",
                    Headers = new Headers
                    {
                        { "eventId", Encoding.UTF8.GetBytes(eventId) },
                        { "x-correlation-id", Encoding.UTF8.GetBytes(eventId) },
                        { "x-failure-reason", Encoding.UTF8.GetBytes("integration test") },
                    },
                });
            }

            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                TestTokens.Create(role: WonrichRoles.SystemAdministrator));

            var allResponse = await client.GetAsync("/api/admin/dead-letters");
            Assert.Equal(HttpStatusCode.OK, allResponse.StatusCode);

            using var allBody = JsonDocument.Parse(await allResponse.Content.ReadAsStringAsync());
            Assert.Contains(
                allBody.RootElement.EnumerateArray(),
                message => message.GetProperty("eventId").GetString() == eventId
                    && message.GetProperty("consumerGroup").GetString() == "traceability-batch-determinations");

            var groupResponse = await client.GetAsync("/api/admin/dead-letters/traceability-stage-events");
            Assert.Equal(HttpStatusCode.NotFound, groupResponse.StatusCode);
            using var groupBody = JsonDocument.Parse(await groupResponse.Content.ReadAsStringAsync());
            Assert.Contains("does not exist", groupBody.RootElement.GetProperty("detail").GetString());
        }
        finally
        {
            try
            {
                await admin.CreateTopicsAsync([
                    new TopicSpecification { Name = MissingTopic, NumPartitions = 3, ReplicationFactor = 1 },
                ]);
            }
            catch (CreateTopicsException ex)
                when (ex.Results.All(r => r.Error.Code == ErrorCode.TopicAlreadyExists))
            {
                // Kafka re-created the topic already (auto-create or a delete still finishing): nothing to do.
            }
        }
    }
}

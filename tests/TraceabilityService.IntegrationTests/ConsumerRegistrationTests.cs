using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TraceabilityService.Application;
using TraceabilityService.Infrastructure.Kafka;

namespace TraceabilityService.IntegrationTests;

/// <summary>
/// The app registers each consumer with the topic, group and dead-letter topic registered in
/// wonrich-infra (SCRUM-126), and runs a consume loop for each.
/// </summary>
[Collection(ApiCollection.Name)]
public class ConsumerRegistrationTests(TraceabilityApiFactory factory)
{
    [Theory]
    [InlineData(ProcessingConsumers.StageEvents,
        "wonrich.processing.stage-events.v1", "traceability-stage-events", "wonrich.dlq.traceability-stage-events.v1")]
    [InlineData(ProcessingConsumers.HoldEvents,
        "wonrich.processing.hold-events.v1", "traceability-hold-events", "wonrich.dlq.traceability-hold-events.v1")]
    [InlineData(LabDeterminationConsumer.Name,
        "wonrich.quality-lab.batch-determinations.v1", "traceability-batch-determinations", "wonrich.dlq.traceability-batch-determinations.v1")]
    [InlineData(ConsignmentTestedConsumer.Name,
        "wonrich.mcc.consignment-tested.v1", "traceability-consignment-tested", "wonrich.dlq.traceability-consignment-tested.v1")]
    public void Each_consumer_uses_its_registered_topic_group_and_dead_letter_topic(
        string consumer, string topic, string group, string deadLetterTopic)
    {
        var options = factory.Services.GetRequiredKeyedService<EventProcessor>(consumer).Options;

        Assert.Equal((topic, group, deadLetterTopic), (options.Topic, options.ConsumerGroup, options.DeadLetterTopic));
    }

    [Fact]
    public void A_consume_loop_runs_for_each_consumer()
    {
        var consumers = factory.Services.GetServices<IHostedService>().OfType<KafkaEventConsumer>();

        Assert.Equal(4, consumers.Count());
    }
}

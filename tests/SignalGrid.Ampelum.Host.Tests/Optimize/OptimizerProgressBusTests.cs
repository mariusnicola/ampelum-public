#pragma warning disable AMPELUM_V09_PREVIEW
using Shouldly;
using SignalGrid.Ampelum.Host.Optimize;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.Optimize;

public sealed class OptimizerProgressBusTests
{
    // The first MoveNextAsync runs the iterator up to its first await, which is after the
    // subscription is registered, so the tests publish only once a subscriber exists.
    static (IAsyncEnumerator<SseEnvelope> Reader, ValueTask<bool> First) StartSubscriber(OptimizerProgressBus bus, string runId)
    {
        var reader = bus.Subscribe(runId, TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        return (reader, reader.MoveNextAsync());
    }

    static async Task<List<SseEnvelope>> DrainAsync(IAsyncEnumerator<SseEnvelope> reader, ValueTask<bool> first)
    {
        var result = new List<SseEnvelope>();
        var hasNext = await first.AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        while (hasNext)
        {
            result.Add(reader.Current);
            hasNext = await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        await reader.DisposeAsync();
        return result;
    }

    [Fact]
    public async Task Publish_BeforeAnySubscriber_IsDropped()
    {
        var bus = new OptimizerProgressBus();
        var runId = "run-1";
        bus.Publish(runId, new SseEnvelope("Early", "{}", 1));

        var (reader, first) = StartSubscriber(bus, runId);
        var late = new SseEnvelope("Late", "{}", 2);
        bus.Publish(runId, late);
        bus.Complete(runId);

        var result = await DrainAsync(reader, first);
        result.ShouldHaveSingleItem();
        result[0].ShouldBe(late);
    }

    [Fact]
    public async Task Subscribe_BeforePublish_ReceivesEnvelope()
    {
        var bus = new OptimizerProgressBus();
        var runId = "run-2";
        var envelope = new SseEnvelope("Event", "{\"data\":1}", null);

        var (reader, first) = StartSubscriber(bus, runId);
        bus.Publish(runId, envelope);
        bus.Complete(runId);

        var result = await DrainAsync(reader, first);
        result.ShouldHaveSingleItem();
        result[0].ShouldBe(envelope);
    }

    [Fact]
    public async Task MultipleSubscribers_SameRunId_BothReceive()
    {
        var bus = new OptimizerProgressBus();
        var runId = "run-3";
        var env1 = new SseEnvelope("Event1", "{}", 1);
        var env2 = new SseEnvelope("Event2", "{}", 2);

        var (reader1, first1) = StartSubscriber(bus, runId);
        var (reader2, first2) = StartSubscriber(bus, runId);
        bus.Publish(runId, env1);
        bus.Publish(runId, env2);
        bus.Complete(runId);

        var results1 = await DrainAsync(reader1, first1);
        var results2 = await DrainAsync(reader2, first2);
        results1.ShouldBe([env1, env2]);
        results2.ShouldBe([env1, env2]);
    }

    [Fact]
    public async Task Complete_CausesSubscriberToExit()
    {
        var bus = new OptimizerProgressBus();
        var runId = "run-4";

        var (reader, first) = StartSubscriber(bus, runId);
        bus.Publish(runId, new SseEnvelope("Event", "{}", null));
        bus.Complete(runId);

        var result = await DrainAsync(reader, first);
        result.ShouldHaveSingleItem();
    }

    [Fact]
    public void Publish_UnknownRunId_DoesNotThrow()
    {
        var bus = new OptimizerProgressBus();
        bus.Publish("unknown-run", new SseEnvelope("E", "{}", 1));
    }

    [Fact]
    public void Complete_UnknownRunId_DoesNotThrow()
    {
        var bus = new OptimizerProgressBus();
        bus.Complete("unknown-run");
    }
}

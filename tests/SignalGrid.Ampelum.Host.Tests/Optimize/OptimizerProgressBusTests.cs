#pragma warning disable AMPELUM_V09_PREVIEW
using Shouldly;
using SignalGrid.Ampelum.Host.Optimize;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.Optimize;

public sealed class OptimizerProgressBusTests
{
    [Fact]
    public async Task Publish_BeforeSubscribe_EnvelopeReceivedAfterSubscribing()
    {
        var bus = new OptimizerProgressBus();
        var runId = "run-1";
        var envelope = new SseEnvelope("TestEvent", "{}", 1);
        var result = new List<SseEnvelope>();

        var subscribeTask = Task.Run(async () =>
        {
            await foreach (var env in bus.Subscribe(runId, TestContext.Current.CancellationToken))
            {
                result.Add(env);
                break;
            }
        }, TestContext.Current.CancellationToken);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        bus.Publish(runId, envelope);
        bus.Complete(runId);

        await subscribeTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        result.ShouldHaveSingleItem();
        result[0].ShouldBe(envelope);
    }

    [Fact]
    public async Task Subscribe_BeforePublish_ReceivesEnvelope()
    {
        var bus = new OptimizerProgressBus();
        var runId = "run-2";
        var envelope = new SseEnvelope("Event", "{\"data\":1}", null);
        var result = new List<SseEnvelope>();

        var subscribeTask = Task.Run(async () =>
        {
            await foreach (var env in bus.Subscribe(runId, TestContext.Current.CancellationToken))
                result.Add(env);
        }, TestContext.Current.CancellationToken);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        bus.Publish(runId, envelope);
        bus.Complete(runId);

        await subscribeTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
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
        var results1 = new List<SseEnvelope>();
        var results2 = new List<SseEnvelope>();

        var sub1 = Task.Run(async () =>
        {
            await foreach (var env in bus.Subscribe(runId, TestContext.Current.CancellationToken))
                results1.Add(env);
        }, TestContext.Current.CancellationToken);
        var sub2 = Task.Run(async () =>
        {
            await foreach (var env in bus.Subscribe(runId, TestContext.Current.CancellationToken))
                results2.Add(env);
        }, TestContext.Current.CancellationToken);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        bus.Publish(runId, env1);
        bus.Publish(runId, env2);
        bus.Complete(runId);

        await Task.WhenAll(sub1, sub2).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        results1.Count.ShouldBe(2);
        results1[0].ShouldBe(env1);
        results1[1].ShouldBe(env2);
        results2.Count.ShouldBe(2);
        results2[0].ShouldBe(env1);
        results2[1].ShouldBe(env2);
    }

    [Fact]
    public async Task Complete_CausesSubscriberToExit()
    {
        var bus = new OptimizerProgressBus();
        var runId = "run-4";
        var result = new List<SseEnvelope>();
        var completed = false;

        var subscribeTask = Task.Run(async () =>
        {
            await foreach (var env in bus.Subscribe(runId, TestContext.Current.CancellationToken))
                result.Add(env);
            completed = true;
        }, TestContext.Current.CancellationToken);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        bus.Publish(runId, new SseEnvelope("Event", "{}", null));
        bus.Complete(runId);

        await subscribeTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        result.ShouldHaveSingleItem();
        completed.ShouldBeTrue();
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

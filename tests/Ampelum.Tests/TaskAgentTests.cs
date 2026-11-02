using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace SignalGrid.Ampelum.Tests;

public sealed class TaskAgentTests : IDisposable
{
    readonly FakeChatClient _fake = new();
    readonly AmpelumContext _ctx;

    public TaskAgentTests()
    {
        _ctx = new AmpelumContext(_fake, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
    }

    [Fact]
    public void TaskAgent_Create_HasIdleStatus()
    {
        using var agent = new TaskAgent(_ctx, "system", null);
        agent.Status.ShouldBe(AgentStatus.Idle);
        agent.Id.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task TaskAgent_Start_TransitionsToRunning()
    {
        _fake.EnqueueStreamingChunks("hello");
        using var agent = new TaskAgent(_ctx, "system", null);
        await agent.StartAsync("do something", TestContext.Current.CancellationToken);
        agent.Status.ShouldBe(AgentStatus.Running);
    }

    [Fact]
    public async Task TaskAgent_Events_FlowThroughChannel()
    {
        _fake.EnqueueStreamingChunks("hello world TASK_COMPLETE done");
        using var agent = new TaskAgent(_ctx, "system", null);
        await agent.StartAsync("test", TestContext.Current.CancellationToken);
        var events = new List<AgentEvent>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var evt in agent.Events.ReadAllAsync(cts.Token))
        {
            events.Add(evt);
            if (evt is CompleteEvent) break;
        }
        events.ShouldNotBeEmpty();
        events.Any(e => e is TokenEvent).ShouldBeTrue();
        events.Any(e => e is CompleteEvent).ShouldBeTrue();
    }

    [Fact]
    public async Task TaskAgent_TaskComplete_EmitsCompleteEvent()
    {
        _fake.EnqueueStreamingChunks("TASK_COMPLETE: finished successfully");
        using var agent = new TaskAgent(_ctx, "system", null);
        await agent.StartAsync("finish", TestContext.Current.CancellationToken);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        AgentEvent? last = null;
        await foreach (var evt in agent.Events.ReadAllAsync(cts.Token))
        {
            last = evt;
            if (evt is CompleteEvent) break;
        }
        last.ShouldBeOfType<CompleteEvent>();
    }

    [Fact]
    public async Task TaskAgent_Stop_TransitionsToStopped()
    {
        _fake.EnqueueStreamingChunks("working...");
        using var agent = new TaskAgent(_ctx, "system", null);
        await agent.StartAsync("long task", TestContext.Current.CancellationToken);
        await agent.StopAsync();
        agent.Status.ShouldBe(AgentStatus.Stopped);
    }

    [Fact]
    public void TaskAgent_Dispose_CleansUp()
    {
        var agent = new TaskAgent(_ctx, "system", null);
        agent.Dispose();
    }

    public void Dispose() { GC.SuppressFinalize(this); }
}

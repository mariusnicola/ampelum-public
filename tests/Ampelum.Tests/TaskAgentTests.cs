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

        // The loop runs in the background and may already have moved on, so check the transition StartAsync
        // records before the loop starts, not the status at an arbitrary later moment.
        agent.Events.TryRead(out var first).ShouldBeTrue();
        first.ShouldBe(new StatusEvent(AgentStatus.Running, "Starting"));
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

    // Stop at many different moments of the loop. The one scripted answer has no completion marker, so after it the
    // loop asks the empty fake again and fails: a Stop that comes first ends Stopped, one that comes after the failure
    // leaves Error. Either way, once Stop has returned the loop is done, the state no longer changes and the last
    // event says the state the agent is in.
    [Fact]
    public async Task TaskAgent_Stop_AtManyMoments_ReturnsAfterTheLoopSettled_WithAStateThatStaysAndMatchesTheLastEvent()
    {
        var wrong = new List<string>();
        for (var i = 0; i < 400; i++)
        {
            var fake = new FakeChatClient();
            fake.EnqueueStreamingChunks("working...");
            var ctx = new AmpelumContext(fake, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
            using var agent = new TaskAgent(ctx, "system", null);
            await agent.StartAsync("long task", TestContext.Current.CancellationToken);
            for (var spin = 0; spin < i % 7; spin++) await Task.Yield();
            if (i % 3 == 0) await Task.Delay(1, TestContext.Current.CancellationToken);

            await agent.StopAsync();
            var atStop = agent.Status;
            // Stop returns once the loop has settled, so everything it wrote is already there to read.
            var events = new List<AgentEvent>();
            while (agent.Events.TryRead(out var evt)) events.Add(evt);
            if (!agent.Events.Completion.IsCompleted) wrong.Add($"run {i}: channel not complete after Stop");
            if (agent.Status != atStop) wrong.Add($"run {i}: {atStop} when Stop returned, {agent.Status} after");

            var last = events.LastOrDefault();
            var matches = atStop switch
            {
                AgentStatus.Stopped => last is StatusEvent { Status: AgentStatus.Stopped },
                AgentStatus.Error => last is ErrorEvent,
                AgentStatus.Completed => last is CompleteEvent,
                _ => false,
            };
            if (!matches) wrong.Add($"run {i}: status {atStop}, last event {last}");
        }
        wrong.ShouldBeEmpty();
    }

    // Forces the order: the model fails after Stop was called, while the loop is inside the model call.
    [Fact]
    public async Task TaskAgent_Stop_WhenTheLoopFailsAfterwards_StaysStopped_AndNeverReportsTheError()
    {
        var model = new GatedFailingClient();
        var ctx = new AmpelumContext(model, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        using var agent = new TaskAgent(ctx, "system", null);
        await agent.StartAsync("long task", TestContext.Current.CancellationToken);
        await model.InsideTheCall.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var stop = agent.StopAsync();
        model.Release.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var events = new List<AgentEvent>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var evt in agent.Events.ReadAllAsync(timeout.Token)) events.Add(evt);
        agent.Status.ShouldBe(AgentStatus.Stopped);
        events.OfType<ErrorEvent>().ShouldBeEmpty();
        events.Last().ShouldBeOfType<StatusEvent>().Status.ShouldBe(AgentStatus.Stopped);
    }

    [Fact]
    public async Task TaskAgent_Stop_AfterTheTaskCompleted_KeepsCompleted()
    {
        _fake.EnqueueStreamingChunks("TASK_COMPLETE: finished");
        using var agent = new TaskAgent(_ctx, "system", null);
        await agent.StartAsync("finish", TestContext.Current.CancellationToken);
        await foreach (var evt in agent.Events.ReadAllAsync(TestContext.Current.CancellationToken))
            if (evt is CompleteEvent) break;

        await agent.StopAsync();

        agent.Status.ShouldBe(AgentStatus.Completed);
    }

    [Fact]
    public async Task TaskAgent_Stop_WhenStartedWithACancelledToken_DoesNotThrow_AndCompletesTheChannel()
    {
        using var agent = new TaskAgent(_ctx, "system", null);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await agent.StartAsync("never runs", cancelled.Token);

        await agent.StopAsync();

        agent.Status.ShouldBe(AgentStatus.Stopped);
        while (agent.Events.TryRead(out _)) { }
        agent.Events.Completion.IsCompleted.ShouldBeTrue();
    }

    sealed class GatedFailingClient : Microsoft.Extensions.AI.IChatClient
    {
        public TaskCompletionSource InsideTheCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, "working");
            InsideTheCall.TrySetResult();
            // Ignores cancellation, as a client in the middle of a network call may, then fails.
            await Release.Task;
            throw new InvalidOperationException("late failure");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    [Fact]
    public void TaskAgent_Dispose_CleansUp()
    {
        var agent = new TaskAgent(_ctx, "system", null);
        agent.Dispose();
    }

    public void Dispose() { GC.SuppressFinalize(this); }
}

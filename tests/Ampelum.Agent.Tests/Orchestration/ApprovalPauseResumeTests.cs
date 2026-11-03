using System.Text.Json;
using SignalGrid.Ampelum.Agent.Contracts;
using SignalGrid.Ampelum.Agent.Orchestration;
using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests.Orchestration;

public sealed class ApprovalPauseResumeTests
{
    [Fact]
    public async Task RunAsync_ApprovalStep_WaitsWithoutCallingTheHandlerWhileSiblingsStillRun()
    {
        var (store, audit, runtime) = Create();
        var request = ApprovalRequest("pause");
        var seen = new List<string>();

        var snapshot = await runtime.RunAsync(request, Record(seen), TestContext.Current.CancellationToken);

        snapshot.Status.ShouldBe(WorkflowExecutionStatus.AwaitingConfirmation);
        snapshot.Nodes["approve"].Status.ShouldBe(DurableWorkflowNodeStatus.Waiting);
        snapshot.Nodes["after"].Status.ShouldBe(DurableWorkflowNodeStatus.Pending);
        seen.ShouldBe(["other"]);
        (await store.LoadAsync("pause", TestContext.Current.CancellationToken))!.Status.ShouldBe(WorkflowExecutionStatus.AwaitingConfirmation);
        var types = audit.Events.Select(e => e.EventType).ToList();
        types.ShouldContain("node.waiting");
        types.Last().ShouldBe("execution.awaiting_confirmation");
        audit.Events.Single(e => e.EventType == "node.waiting").StepId.ShouldBe("approve");
    }

    [Fact]
    public async Task ResumeAsync_WhileStillWaiting_ChangesNothingAndStaysAwaiting()
    {
        var (_, audit, runtime) = Create();
        var request = ApprovalRequest("still-waiting");
        var first = await runtime.RunAsync(request, Record([]), TestContext.Current.CancellationToken);
        var eventsBefore = audit.Events.Count;

        var second = await runtime.ResumeAsync(request, Record([]), TestContext.Current.CancellationToken);

        second.Status.ShouldBe(WorkflowExecutionStatus.AwaitingConfirmation);
        second.Version.ShouldBe(first.Version);
        audit.Events.Count.ShouldBe(eventsBefore);
    }

    [Fact]
    public async Task DecideAsync_ApproveThenResume_FinishesAndRunsOnlyTheStepsAfterTheApproval()
    {
        var (_, audit, runtime) = Create();
        var request = ApprovalRequest("approve");
        var seen = new List<string>();
        await runtime.RunAsync(request, Record(seen), TestContext.Current.CancellationToken);

        var decided = await runtime.DecideAsync("approve", "approve", approved: true, "maria", "looks fine", TestContext.Current.CancellationToken);

        decided.Status.ShouldBe(WorkflowExecutionStatus.Running);
        decided.Nodes["approve"].Status.ShouldBe(DurableWorkflowNodeStatus.Succeeded);
        decided.Nodes["approve"].Decision.ShouldBe(new DurableWorkflowDecision(true, "maria", "looks fine"));
        seen.ShouldBe(["other"]);

        var finished = await runtime.ResumeAsync(request, Record(seen), TestContext.Current.CancellationToken);

        finished.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        seen.ShouldBe(["other", "after"]);
        audit.Events.Select(e => e.EventType).ShouldContain("node.approved");
        audit.Events.Single(e => e.EventType == "node.approved").Detail.ShouldNotBeNull().ShouldContain("maria");
        audit.Events.Last().EventType.ShouldBe("execution.succeeded");
    }

    [Fact]
    public async Task DecideAsync_Reject_FailsTheNodeAndTheExecution()
    {
        var (store, audit, runtime) = Create();
        var request = ApprovalRequest("reject");
        var seen = new List<string>();
        var waiting = await runtime.RunAsync(request, Record(seen), TestContext.Current.CancellationToken);

        var decided = await runtime.DecideAsync("reject", "approve", approved: false, "maria", "too risky", TestContext.Current.CancellationToken);

        // One swap: the node and the execution are both Failed at the very next version.
        decided.Version.ShouldBe(waiting.Version + 1);
        var stored = await store.LoadAsync("reject", TestContext.Current.CancellationToken);
        stored.ShouldNotBeNull();
        stored.Version.ShouldBe(waiting.Version + 1);
        stored.Status.ShouldBe(WorkflowExecutionStatus.Failed);
        stored.Nodes["approve"].Status.ShouldBe(DurableWorkflowNodeStatus.Failed);

        decided.Status.ShouldBe(WorkflowExecutionStatus.Failed);
        decided.FailureReason.ShouldBe("too risky");
        decided.Nodes["approve"].Status.ShouldBe(DurableWorkflowNodeStatus.Failed);
        decided.Nodes["approve"].Detail.ShouldBe("too risky");
        decided.Nodes["approve"].Decision.ShouldNotBeNull().Approved.ShouldBeFalse();
        audit.Events.Select(e => e.EventType).TakeLast(2).ShouldBe(["node.rejected", "execution.failed"]);

        var resumed = await runtime.ResumeAsync(request, Record(seen), TestContext.Current.CancellationToken);
        resumed.Status.ShouldBe(WorkflowExecutionStatus.Failed);
        seen.ShouldBe(["other"]);
    }

    [Fact]
    public async Task DecideAsync_UnknownExecutionUnknownNodeNonWaitingNodeAndFinishedExecution_ThrowLoudly()
    {
        var (_, _, runtime) = Create();
        var request = ApprovalRequest("loud");
        await runtime.RunAsync(request, Record([]), TestContext.Current.CancellationToken);
        var ct = TestContext.Current.CancellationToken;

        (await Should.ThrowAsync<InvalidOperationException>(() => runtime.DecideAsync("nope", "approve", true, "maria", ct: ct)))
            .Message.ShouldContain("unknown");
        (await Should.ThrowAsync<InvalidOperationException>(() => runtime.DecideAsync("loud", "missing", true, "maria", ct: ct)))
            .Message.ShouldContain("no node 'missing'");
        (await Should.ThrowAsync<InvalidOperationException>(() => runtime.DecideAsync("loud", "after", true, "maria", ct: ct)))
            .Message.ShouldContain("not Waiting");
        (await Should.ThrowAsync<InvalidOperationException>(() => runtime.DecideAsync("loud", "other", true, "maria", ct: ct)))
            .Message.ShouldContain("not Waiting");

        await runtime.DecideAsync("loud", "approve", false, "maria", ct: ct);
        (await Should.ThrowAsync<InvalidOperationException>(() => runtime.DecideAsync("loud", "approve", true, "maria", ct: ct)))
            .Message.ShouldContain("already Failed");
    }

    [Fact]
    public async Task DecideAsync_HandlerAskedToWait_ApprovalGoesBackToPendingAndTheNextCallCarriesTheDecision()
    {
        var (_, audit, runtime) = Create();
        var request = new WorkflowExecutionRequest("handler-wait", new WorkflowDefinition("wf", "v1", "Test",
            [new WorkflowStepDefinition("deploy", "deploy", [])]), "tester", DateTimeOffset.UtcNow);
        var contexts = new List<DurableWorkflowNodeContext>();
        Task<DurableWorkflowNodeResult> Handler(DurableWorkflowNodeContext context, CancellationToken _)
        {
            contexts.Add(context);
            return Task.FromResult(context.Decision is null ? DurableWorkflowNodeResult.Wait("needs a person") : DurableWorkflowNodeResult.Success("deployed"));
        }

        var waiting = await runtime.RunAsync(request, Handler, TestContext.Current.CancellationToken);
        waiting.Status.ShouldBe(WorkflowExecutionStatus.AwaitingConfirmation);
        waiting.Nodes["deploy"].Status.ShouldBe(DurableWorkflowNodeStatus.Waiting);
        waiting.Nodes["deploy"].Detail.ShouldBe("needs a person");

        var decided = await runtime.DecideAsync("handler-wait", "deploy", true, "maria", ct: TestContext.Current.CancellationToken);
        decided.Nodes["deploy"].Status.ShouldBe(DurableWorkflowNodeStatus.Pending);
        decided.Nodes["deploy"].Decision.ShouldNotBeNull().DecidedBy.ShouldBe("maria");

        var finished = await runtime.ResumeAsync(request, Handler, TestContext.Current.CancellationToken);

        finished.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        contexts.Count.ShouldBe(2);
        contexts[0].Decision.ShouldBeNull();
        contexts[1].Decision.ShouldNotBeNull().Approved.ShouldBeTrue();
        contexts[1].IdempotencyKey.ShouldBe(contexts[0].IdempotencyKey);
        audit.Events.Select(e => e.EventType).ShouldBe(
            ["execution.started", "node.started", "node.waiting", "execution.awaiting_confirmation", "node.approved", "node.started", "node.succeeded", "execution.succeeded"]);
    }

    [Fact]
    public async Task ResumeAsync_AfterACrash_KeepsAWaitingNodeWaitingAndOnlyRecoversTheRunningOne()
    {
        var store = new InMemoryWorkflowStateStore();
        var audit = new InMemoryWorkflowRuntimeAuditSink();
        var request = new WorkflowExecutionRequest("crash", new WorkflowDefinition("wf", "v1", "Test",
        [
            new WorkflowStepDefinition("asks", "asks", []),
            new WorkflowStepDefinition("busy", "busy", []),
        ]), "tester", DateTimeOffset.UtcNow);
        var nodes = new Dictionary<string, DurableWorkflowNodeSnapshot>
        {
            ["asks"] = new("asks", DurableWorkflowNodeStatus.Waiting, 1, "crash:asks", "needs a person"),
            ["busy"] = new("busy", DurableWorkflowNodeStatus.Running, 1, "crash:busy"),
        };
        await store.TryCreateAsync(new DurableWorkflowSnapshot(request, WorkflowExecutionStatus.Running, nodes, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        var seen = new List<string>();

        var resumed = await new DurableWorkflowRuntime(store, audit).ResumeAsync(request, Record(seen), TestContext.Current.CancellationToken);

        resumed.Nodes["asks"].Status.ShouldBe(DurableWorkflowNodeStatus.Waiting);
        resumed.Nodes["busy"].Status.ShouldBe(DurableWorkflowNodeStatus.Succeeded);
        resumed.Status.ShouldBe(WorkflowExecutionStatus.AwaitingConfirmation);
        seen.ShouldBe(["busy"]);
    }

    [Fact]
    public async Task SlowStepOutcome_IsNotLostWhenADecisionIsDeliveredWhileItsHandlerRuns()
    {
        var (_, _, runtime) = Create();
        var ct = TestContext.Current.CancellationToken;
        var request = new WorkflowExecutionRequest("slow-and-decide", new WorkflowDefinition("wf", "v1", "Test",
        [
            new WorkflowStepDefinition("approve", "approve", [], Kind: WorkflowStepKind.Approval),
            new WorkflowStepDefinition("slow", "slow", []),
            new WorkflowStepDefinition("after", "after", ["approve"]),
        ]), "tester", DateTimeOffset.UtcNow);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowCalls = 0;
        var afterCalls = 0;
        async Task<DurableWorkflowNodeResult> Handler(DurableWorkflowNodeContext context, CancellationToken token)
        {
            if (context.Step.StepId == "slow")
            {
                Interlocked.Increment(ref slowCalls);
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            else
            {
                Interlocked.Increment(ref afterCalls);
            }
            return DurableWorkflowNodeResult.Success();
        }

        var run = runtime.RunAsync(request, Handler, ct);
        await entered.Task.WaitAsync(ct);
        await runtime.DecideAsync("slow-and-decide", "approve", true, "maria", ct: ct);
        release.SetResult();
        var snapshot = await run.WaitAsync(ct);

        snapshot.Nodes["slow"].Status.ShouldBe(DurableWorkflowNodeStatus.Succeeded);
        slowCalls.ShouldBe(1);
        var finished = await runtime.ResumeAsync(request, Handler, ct);
        finished.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        finished.Nodes.Values.ShouldAllBe(node => node.Status == DurableWorkflowNodeStatus.Succeeded);
        slowCalls.ShouldBe(1);
        afterCalls.ShouldBe(1);
    }

    [Fact]
    public async Task ResumeAsync_RunningExecutionWithAFailedNodeAndNothingToRun_EndsFailedNotSucceeded()
    {
        var store = new InMemoryWorkflowStateStore();
        var request = new WorkflowExecutionRequest("failed-node", new WorkflowDefinition("wf", "v1", "Test",
            [new WorkflowStepDefinition("broken", "broken", [])]), "tester", DateTimeOffset.UtcNow);
        var nodes = new Dictionary<string, DurableWorkflowNodeSnapshot>
        {
            ["broken"] = new("broken", DurableWorkflowNodeStatus.Failed, 1, "failed-node:broken", "it broke"),
        };
        await store.TryCreateAsync(new DurableWorkflowSnapshot(request, WorkflowExecutionStatus.Running, nodes, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);

        var resumed = await new DurableWorkflowRuntime(store, new InMemoryWorkflowRuntimeAuditSink()).ResumeAsync(request, Record([]), TestContext.Current.CancellationToken);

        resumed.Status.ShouldBe(WorkflowExecutionStatus.Failed);
        resumed.FailureReason.ShouldBe("it broke");
    }

    [Fact]
    public async Task LoopStepNeedingConfirmation_AsksAgainOnTheNextIteration()
    {
        var (_, _, runtime) = Create();
        var ct = TestContext.Current.CancellationToken;
        var dispatcher = new CountingDispatcher();
        var tool = new WorkflowToolStepHandler(dispatcher, Caller);
        var request = new WorkflowExecutionRequest("loop-approve", new WorkflowDefinition("wf", "v1", "Test",
            [new WorkflowStepDefinition("sweep", "delete_record", [], Kind: WorkflowStepKind.Loop, MaxIterations: 2, Effect: EffectClass.Mutating)],
            Policy: new WorkflowPolicyBinding(EffectClass.Irreversible, RequireApprovalFor: [EffectClass.Mutating])), "tester", DateTimeOffset.UtcNow);
        // The tool handler only takes Tool-kind steps, so the loop step is presented to it as one.
        async Task<DurableWorkflowNodeResult> Handler(DurableWorkflowNodeContext context, CancellationToken token)
        {
            var result = await tool.HandleAsync(context with { Step = context.Step with { Kind = WorkflowStepKind.Tool } }, token);
            return result with { LoopContinue = true };
        }

        var first = await runtime.RunAsync(request, Handler, ct);
        first.Status.ShouldBe(WorkflowExecutionStatus.AwaitingConfirmation);
        await runtime.DecideAsync("loop-approve", "sweep", true, "maria", ct: ct);

        var second = await runtime.ResumeAsync(request, Handler, ct);

        dispatcher.InvocationIds.Count.ShouldBe(1);
        second.Status.ShouldBe(WorkflowExecutionStatus.AwaitingConfirmation);
        second.Nodes["sweep"].Status.ShouldBe(DurableWorkflowNodeStatus.Waiting);
        second.Nodes["sweep"].Decision.ShouldBeNull();
        second.Nodes["sweep"].LoopIterations.ShouldBe(1);

        await runtime.DecideAsync("loop-approve", "sweep", true, "maria", ct: ct);
        var third = await runtime.ResumeAsync(request, Handler, ct);

        third.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        dispatcher.InvocationIds.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ToolStepNeedingConfirmation_WaitsThenRunsTheToolOnceWithTheSameIdempotencyKey()
    {
        var (_, _, runtime) = Create();
        var dispatcher = new CountingDispatcher();
        var handler = new WorkflowToolStepHandler(dispatcher, Caller);
        var request = ToolRequest("tool-approve");

        var waiting = await runtime.RunAsync(request, (context, ct) => handler.HandleAsync(context, ct), TestContext.Current.CancellationToken);

        waiting.Status.ShouldBe(WorkflowExecutionStatus.AwaitingConfirmation);
        waiting.Nodes["delete"].Status.ShouldBe(DurableWorkflowNodeStatus.Waiting);
        dispatcher.InvocationIds.ShouldBeEmpty();
        var keyWhileWaiting = waiting.Nodes["delete"].IdempotencyKey;

        await runtime.DecideAsync("tool-approve", "delete", true, "maria", ct: TestContext.Current.CancellationToken);
        var finished = await runtime.ResumeAsync(request, (context, ct) => handler.HandleAsync(context, ct), TestContext.Current.CancellationToken);

        finished.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        dispatcher.InvocationIds.ShouldBe([keyWhileWaiting]);
        finished.Nodes["delete"].IdempotencyKey.ShouldBe(keyWhileWaiting);
    }

    [Fact]
    public async Task ToolStepNeedingConfirmation_Rejected_NeverRunsTheTool()
    {
        var (_, _, runtime) = Create();
        var dispatcher = new CountingDispatcher();
        var handler = new WorkflowToolStepHandler(dispatcher, Caller);
        var request = ToolRequest("tool-reject");
        await runtime.RunAsync(request, (context, ct) => handler.HandleAsync(context, ct), TestContext.Current.CancellationToken);

        var decided = await runtime.DecideAsync("tool-reject", "delete", false, "maria", "no", TestContext.Current.CancellationToken);
        var resumed = await runtime.ResumeAsync(request, (context, ct) => handler.HandleAsync(context, ct), TestContext.Current.CancellationToken);

        decided.Status.ShouldBe(WorkflowExecutionStatus.Failed);
        resumed.Status.ShouldBe(WorkflowExecutionStatus.Failed);
        dispatcher.InvocationIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task HandleAsync_BlockedStep_StillFailsEvenWithAnApprovalInTheContext()
    {
        var dispatcher = new CountingDispatcher();
        var handler = new WorkflowToolStepHandler(dispatcher, Caller);
        var step = new WorkflowStepDefinition("delete", "delete_record", [], Kind: WorkflowStepKind.Tool, MaxAutonomy: AutonomyMode.Forbidden);
        var request = new WorkflowExecutionRequest("blocked", new WorkflowDefinition("wf", "v1", "Test", [step]), "tester", DateTimeOffset.UtcNow);

        var result = await handler.HandleAsync(
            new DurableWorkflowNodeContext(request, step, "blocked:delete", 1, new DurableWorkflowDecision(true, "maria")),
            TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeFalse();
        result.Waiting.ShouldBeFalse();
        dispatcher.InvocationIds.ShouldBeEmpty();
    }

    internal static readonly AgentIdentity Caller = new("test-bot", Guid.NewGuid().ToString(), [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));

    internal static (InMemoryWorkflowStateStore Store, InMemoryWorkflowRuntimeAuditSink Audit, DurableWorkflowRuntime Runtime) Create()
    {
        var store = new InMemoryWorkflowStateStore();
        var audit = new InMemoryWorkflowRuntimeAuditSink();
        return (store, audit, new DurableWorkflowRuntime(store, audit));
    }

    /// <summary>approve (Approval) then after; other is an independent ordinary step.</summary>
    internal static WorkflowExecutionRequest ApprovalRequest(string executionId) => new(
        executionId,
        new WorkflowDefinition("wf", "v1", "Test",
        [
            new WorkflowStepDefinition("approve", "approve", [], Kind: WorkflowStepKind.Approval, MaxAutonomy: AutonomyMode.RequireApproval),
            new WorkflowStepDefinition("other", "other", []),
            new WorkflowStepDefinition("after", "after", ["approve"]),
        ]),
        "tester",
        DateTimeOffset.UtcNow);

    internal static Func<DurableWorkflowNodeContext, CancellationToken, Task<DurableWorkflowNodeResult>> Record(List<string> seen) =>
        (context, _) =>
        {
            lock (seen) seen.Add(context.Step.StepId);
            return Task.FromResult(DurableWorkflowNodeResult.Success());
        };

    static WorkflowExecutionRequest ToolRequest(string executionId) => new(
        executionId,
        new WorkflowDefinition("wf", "v1", "Test",
            [new WorkflowStepDefinition("delete", "delete_record", [], Kind: WorkflowStepKind.Tool, Effect: EffectClass.Mutating)],
            Policy: new WorkflowPolicyBinding(EffectClass.Irreversible, RequireApprovalFor: [EffectClass.Mutating])),
        "tester",
        DateTimeOffset.UtcNow);

    sealed class CountingDispatcher : IToolDispatcher
    {
        public List<string?> InvocationIds { get; } = [];

        public Task<ToolResult> InvokeAsync(string toolName, IReadOnlyDictionary<string, JsonElement> args, AgentIdentity caller, CancellationToken ct = default) =>
            InvokeAsync(toolName, args, caller, context: null, ct);

        public Task<ToolResult> InvokeAsync(string toolName, IReadOnlyDictionary<string, JsonElement> args, AgentIdentity caller, ToolInvocationContext? context, CancellationToken ct = default)
        {
            InvocationIds.Add(context?.InvocationId);
            return Task.FromResult(new ToolResult(toolName, true, "done", null, DateTimeOffset.UtcNow, TimeSpan.Zero));
        }
    }
}

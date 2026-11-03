using SignalGrid.Ampelum.Agent.Contracts;
using SignalGrid.Ampelum.Agent.Orchestration;
using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests.Orchestration;

/// <summary>Stands for the process dying right after a write.</summary>
internal sealed class SimulatedCrashException : Exception;

/// <summary>Forwards to a plain audit sink and throws once the matching event has been appended.</summary>
internal sealed class CrashingAuditSink(IWorkflowRuntimeAuditSink inner, Func<WorkflowRuntimeAuditEvent, bool> crashAfter) : IWorkflowRuntimeAuditSink
{
    public async Task AppendAsync(WorkflowRuntimeAuditEvent auditEvent, CancellationToken ct = default)
    {
        await inner.AppendAsync(auditEvent, ct);
        if (crashAfter(auditEvent)) throw new SimulatedCrashException();
    }
}

/// <summary>Forwards to a plain audit sink and runs a hook after the matching event has been appended.</summary>
internal sealed class HookedAuditSink(IWorkflowRuntimeAuditSink inner, string eventType, Action hook) : IWorkflowRuntimeAuditSink
{
    public async Task AppendAsync(WorkflowRuntimeAuditEvent auditEvent, CancellationToken ct = default)
    {
        await inner.AppendAsync(auditEvent, ct);
        if (auditEvent.EventType == eventType) hook();
    }
}

/// <summary>Makes the first write of one checkpoint lose its version race: a competing write lands first, as another process would.</summary>
internal sealed class RaceOnceStore(IWorkflowStateStore inner, Func<DurableWorkflowSnapshot, DurableWorkflowSnapshot, bool> when) : IWorkflowStateStore
{
    bool _raced;

    public Task<DurableWorkflowSnapshot?> LoadAsync(string executionId, CancellationToken ct = default) => inner.LoadAsync(executionId, ct);

    public Task<bool> TryCreateAsync(DurableWorkflowSnapshot snapshot, CancellationToken ct = default) => inner.TryCreateAsync(snapshot, ct);

    public async Task<bool> TryUpdateAsync(string executionId, DurableWorkflowSnapshot expected, DurableWorkflowSnapshot next, CancellationToken ct = default)
    {
        if (!_raced && when(expected, next))
        {
            _raced = true;
            (await inner.TryUpdateAsync(executionId, expected, expected with { Version = expected.Version + 1 }, ct)).ShouldBeTrue();
        }
        return await inner.TryUpdateAsync(executionId, expected, next, ct);
    }
}

/// <summary>Runs an action once, just before the first write that matches, so a competing write can land in between.</summary>
internal sealed class BeforeUpdateStore(IWorkflowStateStore inner, Func<DurableWorkflowSnapshot, bool> when, Func<Task> before) : IWorkflowStateStore
{
    bool _done;

    public Task<DurableWorkflowSnapshot?> LoadAsync(string executionId, CancellationToken ct = default) => inner.LoadAsync(executionId, ct);

    public Task<bool> TryCreateAsync(DurableWorkflowSnapshot snapshot, CancellationToken ct = default) => inner.TryCreateAsync(snapshot, ct);

    public async Task<bool> TryUpdateAsync(string executionId, DurableWorkflowSnapshot expected, DurableWorkflowSnapshot next, CancellationToken ct = default)
    {
        if (!_done && when(next))
        {
            _done = true;
            await before();
        }
        return await inner.TryUpdateAsync(executionId, expected, next, ct);
    }
}

/// <summary>Forwards to a transactional store and throws once a write that carried the matching event has committed.</summary>
internal sealed class CrashingTransactionStore(IWorkflowCheckpointTransactionStore inner, Func<WorkflowRuntimeAuditEvent, bool> crashAfter) : IWorkflowCheckpointTransactionStore
{
    public Task<DurableWorkflowSnapshot?> LoadAsync(string executionId, CancellationToken ct = default) => inner.LoadAsync(executionId, ct);

    public Task<bool> TryCreateAsync(DurableWorkflowSnapshot snapshot, CancellationToken ct = default) => inner.TryCreateAsync(snapshot, ct);

    public Task<bool> TryUpdateAsync(string executionId, DurableWorkflowSnapshot expected, DurableWorkflowSnapshot next, CancellationToken ct = default) =>
        inner.TryUpdateAsync(executionId, expected, next, ct);

    public async Task<bool> TryCreateAndAppendAsync(DurableWorkflowSnapshot snapshot, WorkflowRuntimeAuditEvent auditEvent, CancellationToken ct = default)
    {
        var created = await inner.TryCreateAndAppendAsync(snapshot, auditEvent, ct);
        if (created && crashAfter(auditEvent)) throw new SimulatedCrashException();
        return created;
    }

    public async Task<bool> TryUpdateAndAppendAsync(string executionId, DurableWorkflowSnapshot expected, DurableWorkflowSnapshot next, WorkflowRuntimeAuditEvent auditEvent, CancellationToken ct = default)
    {
        var updated = await inner.TryUpdateAndAppendAsync(executionId, expected, next, auditEvent, ct);
        if (updated && crashAfter(auditEvent)) throw new SimulatedCrashException();
        return updated;
    }

    public async Task AppendAsync(WorkflowRuntimeAuditEvent auditEvent, CancellationToken ct = default)
    {
        await inner.AppendAsync(auditEvent, ct);
        if (crashAfter(auditEvent)) throw new SimulatedCrashException();
    }
}

/// <summary>A place checkpoints live. Every open gives new persistence objects over the same data, as a restarted process would.</summary>
internal sealed class StepKindBackend(
    Func<Func<WorkflowRuntimeAuditEvent, bool>?, (IWorkflowStateStore Store, IWorkflowRuntimeAuditSink Audit)> open,
    Func<string, CancellationToken, Task<IReadOnlyList<WorkflowRuntimeAuditEvent>>> readAudit,
    Func<string, string> newExecutionId)
{
    /// <summary>A runtime on new persistence objects; with <paramref name="crashAfter"/> the process dies right after the matching event.</summary>
    public DurableWorkflowRuntime Runtime(Func<WorkflowRuntimeAuditEvent, bool>? crashAfter = null, IDurableWorkflowEvaluator? evaluator = null)
    {
        var (store, audit) = open(crashAfter);
        return new DurableWorkflowRuntime(store, audit, evaluator: evaluator);
    }

    public IWorkflowStateStore Store => open(null).Store;

    public Task<DurableWorkflowSnapshot?> LoadAsync(string executionId) => open(null).Store.LoadAsync(executionId, TestContext.Current.CancellationToken);

    public Task<IReadOnlyList<WorkflowRuntimeAuditEvent>> EventsAsync(string executionId) => readAudit(executionId, TestContext.Current.CancellationToken);

    public async Task<List<string>> AuditAsync(string executionId) =>
        [.. (await readAudit(executionId, TestContext.Current.CancellationToken)).Select(e => e.StepId is null ? e.EventType : $"{e.EventType}:{e.StepId}")];

    public string NewId(string label) => newExecutionId(label);

    public static Func<WorkflowRuntimeAuditEvent, bool> After(string eventType, string? stepId = null) =>
        e => e.EventType == eventType && (stepId is null || e.StepId == stepId);
}

internal sealed class ScriptedEvaluator(Func<DurableWorkflowEvaluationContext, IReadOnlyList<MetricObservation>> script) : IDurableWorkflowEvaluator
{
    public List<DurableWorkflowEvaluationContext> Calls { get; } = [];

    public Task<IReadOnlyList<MetricObservation>> EvaluateAsync(DurableWorkflowEvaluationContext context, CancellationToken ct)
    {
        lock (Calls) Calls.Add(context);
        return Task.FromResult(script(context));
    }

    public static MetricObservation Observe(string metricId, double value) => new(metricId, value, DateTimeOffset.UtcNow, "test");
}

/// <summary>A checkpoint as an earlier release wrote it: the node fields added for the new step kinds are absent from the JSON.</summary>
internal static class LegacyCheckpoint
{
    public static string Json(DurableWorkflowSnapshot snapshot)
    {
        var json = System.Text.Json.JsonSerializer.SerializeToNode(snapshot, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        foreach (var (_, node) in json["nodes"]!.AsObject())
        {
            var fields = node!.AsObject();
            fields.Remove("signal").ShouldBeTrue();
            fields.Remove("observations").ShouldBeTrue();
            fields.Remove("completionOrder").ShouldBeTrue();
        }
        return json.ToJsonString();
    }
}

/// <summary>
/// One run, one crash at the point that matters for each kind, one resume. They run on any backend: in memory,
/// SQLite and PostgreSQL share the assertions, so a store cannot pass by behaving differently.
/// </summary>
internal static class StepKindScenarios
{
    static WorkflowExecutionRequest Request(string id, WorkflowDefinition definition) => new(id, definition, "tester", DateTimeOffset.UtcNow);

    public static async Task SignalSurvivesACrashAfterDelivery(StepKindBackend backend)
    {
        var ct = TestContext.Current.CancellationToken;
        var id = backend.NewId("kind-signal-");
        var request = Request(id, new WorkflowDefinition("wf", "v1", "Signal",
        [
            new WorkflowStepDefinition("prep", "prep", []),
            new WorkflowStepDefinition("receipt", "receipt", ["prep"], new Dictionary<string, string> { ["signal"] = "receipt-arrived" }, Kind: WorkflowStepKind.WaitForSignal),
            new WorkflowStepDefinition("use", "use", ["receipt"]),
        ]));
        var calls = new List<string>();
        string? payload = null;
        Task<DurableWorkflowNodeResult> Handler(DurableWorkflowNodeContext context, CancellationToken _)
        {
            lock (calls) calls.Add(context.Step.StepId);
            if (context.Step.StepId == "use") payload = context.Dependencies["receipt"].Signal?.Payload;
            return Task.FromResult(DurableWorkflowNodeResult.Success());
        }

        var waiting = await backend.Runtime().RunAsync(request, Handler, ct);
        waiting.Status.ShouldBe(WorkflowExecutionStatus.AwaitingConfirmation);
        waiting.Nodes["receipt"].Status.ShouldBe(DurableWorkflowNodeStatus.Waiting);

        await Should.ThrowAsync<SimulatedCrashException>(() =>
            backend.Runtime(StepKindBackend.After("signal.received")).SignalAsync(id, "receipt-arrived", "42.50", "scanner", ct));

        var stored = await backend.LoadAsync(id);
        stored.ShouldNotBeNull().Status.ShouldBe(WorkflowExecutionStatus.Running);
        stored.Nodes["receipt"].Status.ShouldBe(DurableWorkflowNodeStatus.Succeeded);
        stored.Nodes["receipt"].Signal.ShouldBe(new DurableWorkflowSignal("receipt-arrived", "42.50", "scanner"));
        stored.Nodes["receipt"].CompletionOrder.ShouldBeGreaterThan(stored.Nodes["prep"].CompletionOrder);

        var finished = await backend.Runtime().ResumeAsync(request, Handler, ct);

        finished.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        calls.ShouldBe(["prep", "use"]);
        payload.ShouldBe("42.50");
        (await backend.AuditAsync(id)).ShouldBe(
        [
            "execution.started", "node.started:prep", "node.succeeded:prep", "node.waiting:receipt", "execution.awaiting_confirmation",
            "signal.received:receipt", "node.started:use", "node.succeeded:use", "execution.succeeded",
        ]);
    }

    public static async Task EvaluateSurvivesACrashAfterItsObservationsWereWritten(StepKindBackend backend)
    {
        var ct = TestContext.Current.CancellationToken;
        var id = backend.NewId("kind-evaluate-");
        var request = Request(id, EvaluateDefinition());
        var evaluator = new ScriptedEvaluator(_ => [ScriptedEvaluator.Observe("score", 0.9)]);
        var calls = new List<string>();
        Task<DurableWorkflowNodeResult> Handler(DurableWorkflowNodeContext context, CancellationToken _)
        {
            lock (calls) calls.Add(context.Step.StepId);
            return Task.FromResult(DurableWorkflowNodeResult.Success());
        }

        await Should.ThrowAsync<SimulatedCrashException>(() =>
            backend.Runtime(StepKindBackend.After("node.evaluated"), evaluator).RunAsync(request, Handler, ct));

        var stored = await backend.LoadAsync(id);
        stored.ShouldNotBeNull().Status.ShouldBe(WorkflowExecutionStatus.Running);
        stored.Nodes["eval"].Status.ShouldBe(DurableWorkflowNodeStatus.Succeeded);
        var observation = stored.Nodes["eval"].Observations.ShouldNotBeNull().ShouldHaveSingleItem();
        observation.MetricId.ShouldBe("score");
        observation.Value.ShouldBe(0.9);
        observation.Source.ShouldBe("test");

        var finished = await backend.Runtime(evaluator: evaluator).ResumeAsync(request, Handler, ct);

        finished.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        evaluator.Calls.Count.ShouldBe(1);
        finished.Nodes["eval"].Attempts.ShouldBe(1);
        calls.ShouldBe(["work", "after"]);
        // The observations are in the audit event of the swap that stored them, so the crash lost nothing.
        (await backend.EventsAsync(id)).Single(e => e.EventType == "node.evaluated").Detail.ShouldBe("score=0.9 (met maximize threshold 0.8)");
        (await backend.AuditAsync(id)).ShouldBe(
        [
            "execution.started", "node.started:work", "node.succeeded:work", "node.started:eval", "node.evaluated:eval",
            "node.started:after", "node.succeeded:after", "execution.succeeded",
        ]);
    }

    public static WorkflowDefinition EvaluateDefinition(double threshold = 0.8, string direction = "maximize") => new("wf", "v1", "Evaluate",
        [
            new WorkflowStepDefinition("work", "work", []),
            new WorkflowStepDefinition("eval", "eval", ["work"], Kind: WorkflowStepKind.Evaluate),
            new WorkflowStepDefinition("after", "after", ["eval"]),
        ],
        Objective: new WorkflowObjective([new WorkflowObjectiveMetric("score", direction, threshold)]),
        EvaluationPlan: new WorkflowEvaluationPlan(["score"]));

    public static async Task EmitEventIsWrittenOnceAcrossACrashAndResume(StepKindBackend backend)
    {
        var ct = TestContext.Current.CancellationToken;
        var id = backend.NewId("kind-emit-");
        var request = Request(id, new WorkflowDefinition("wf", "v1", "Emit",
        [
            new WorkflowStepDefinition("a", "a", []),
            new WorkflowStepDefinition("emit", "emit", ["a"], new Dictionary<string, string> { ["event"] = "order.shipped", ["detail"] = "box 7" }, Kind: WorkflowStepKind.EmitEvent),
            new WorkflowStepDefinition("b", "b", ["emit"]),
        ]));
        var calls = new List<string>();
        Task<DurableWorkflowNodeResult> Handler(DurableWorkflowNodeContext context, CancellationToken _)
        {
            lock (calls) calls.Add(context.Step.StepId);
            return Task.FromResult(DurableWorkflowNodeResult.Success());
        }

        await Should.ThrowAsync<SimulatedCrashException>(() =>
            backend.Runtime(StepKindBackend.After("event.order.shipped")).RunAsync(request, Handler, ct));
        var stored = await backend.LoadAsync(id);
        stored.ShouldNotBeNull().Nodes["emit"].Status.ShouldBe(DurableWorkflowNodeStatus.Succeeded);

        var finished = await backend.Runtime().ResumeAsync(request, Handler, ct);

        finished.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        finished.Nodes["emit"].Detail.ShouldBe("box 7");
        finished.Nodes["emit"].Attempts.ShouldBe(0);
        calls.ShouldBe(["a", "b"]);
        var audit = await backend.AuditAsync(id);
        audit.Count(e => e.StartsWith("event.", StringComparison.Ordinal)).ShouldBe(1);
        audit.ShouldBe(
        [
            "execution.started", "node.started:a", "node.succeeded:a", "event.order.shipped:emit", "node.started:b", "node.succeeded:b", "execution.succeeded",
        ]);
    }

    public static WorkflowDefinition CompensationDefinition() => new("wf", "v1", "Saga",
    [
        new WorkflowStepDefinition("undo-book", "undo-book", [], Kind: WorkflowStepKind.Compensate),
        new WorkflowStepDefinition("undo-pay", "undo-pay", [], Kind: WorkflowStepKind.Compensate),
        new WorkflowStepDefinition("book", "book", [], CompensationStepId: "undo-book"),
        new WorkflowStepDefinition("pay", "pay", ["book"], CompensationStepId: "undo-pay"),
        new WorkflowStepDefinition("ship", "ship", ["pay"]),
    ]);

    public static async Task CompensationContinuesAfterACrashBetweenTwoCompensations(StepKindBackend backend)
    {
        var ct = TestContext.Current.CancellationToken;
        var id = backend.NewId("kind-compensate-");
        var request = Request(id, CompensationDefinition());
        var calls = new List<string>();
        var undone = new Dictionary<string, string>();
        Task<DurableWorkflowNodeResult> Handler(DurableWorkflowNodeContext context, CancellationToken _)
        {
            lock (calls) calls.Add(context.Step.StepId);
            if (context.Step.Kind == WorkflowStepKind.Compensate)
                lock (undone) undone[context.Step.StepId] = context.Dependencies.Keys.Single();
            return Task.FromResult(context.Step.StepId == "ship" ? DurableWorkflowNodeResult.Failure("carrier rejected") : DurableWorkflowNodeResult.Success());
        }

        await Should.ThrowAsync<SimulatedCrashException>(() =>
            backend.Runtime(StepKindBackend.After("node.succeeded", "undo-pay")).RunAsync(request, Handler, ct));
        var stored = await backend.LoadAsync(id);
        stored.ShouldNotBeNull().Status.ShouldBe(WorkflowExecutionStatus.Running);
        stored.Nodes["ship"].Status.ShouldBe(DurableWorkflowNodeStatus.Failed);
        stored.Nodes["undo-pay"].Status.ShouldBe(DurableWorkflowNodeStatus.Succeeded);
        stored.Nodes["undo-book"].Status.ShouldBe(DurableWorkflowNodeStatus.Pending);

        var finished = await backend.Runtime().ResumeAsync(request, Handler, ct);

        finished.Status.ShouldBe(WorkflowExecutionStatus.Failed);
        finished.FailureReason.ShouldBe("carrier rejected");
        finished.Nodes["undo-pay"].Status.ShouldBe(DurableWorkflowNodeStatus.Succeeded);
        finished.Nodes["undo-book"].Status.ShouldBe(DurableWorkflowNodeStatus.Succeeded);
        calls.ShouldBe(["book", "pay", "ship", "undo-pay", "undo-book"]);
        undone.ShouldBe(new Dictionary<string, string> { ["undo-pay"] = "pay", ["undo-book"] = "book" });
        (await backend.AuditAsync(id)).ShouldBe(
        [
            "execution.started", "node.started:book", "node.succeeded:book", "node.started:pay", "node.succeeded:pay", "node.started:ship", "node.failed:ship",
            "node.started:undo-pay", "node.succeeded:undo-pay", "node.started:undo-book", "node.succeeded:undo-book", "execution.failed",
        ]);
    }
}

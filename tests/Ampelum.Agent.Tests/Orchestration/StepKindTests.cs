using SignalGrid.Ampelum.Agent.Contracts;
using SignalGrid.Ampelum.Agent.Orchestration;
using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests.Orchestration;

/// <summary>WaitForSignal, Evaluate, EmitEvent and Compensate on the in-memory store. SQLite and PostgreSQL run the crash scenarios.</summary>
public sealed class StepKindTests
{
    static readonly Func<DurableWorkflowNodeContext, CancellationToken, Task<DurableWorkflowNodeResult>> Succeeds =
        (_, _) => Task.FromResult(DurableWorkflowNodeResult.Success());

    static (InMemoryWorkflowStateStore Store, InMemoryWorkflowRuntimeAuditSink Sink, StepKindBackend Backend) Create()
    {
        var store = new InMemoryWorkflowStateStore();
        var sink = new InMemoryWorkflowRuntimeAuditSink();
        var backend = new StepKindBackend(
            crash => (store, crash is null ? sink : new CrashingAuditSink(sink, crash)),
            (id, _) => Task.FromResult<IReadOnlyList<WorkflowRuntimeAuditEvent>>([.. sink.Events.Where(e => e.ExecutionId == id)]),
            label => label + Guid.NewGuid().ToString("N"));
        return (store, sink, backend);
    }

    static WorkflowExecutionRequest Request(string id, WorkflowDefinition definition) => new(id, definition, "tester", DateTimeOffset.UtcNow);

    static IReadOnlyDictionary<string, string> Inputs(params (string Key, string Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    // ---- crash scenarios --------------------------------------------------------------------------------------

    [Fact]
    public async Task WaitForSignal_CrashAfterDelivery_ResumesWithoutCallingTheHandlerForIt() =>
        await StepKindScenarios.SignalSurvivesACrashAfterDelivery(Create().Backend);

    [Fact]
    public async Task Evaluate_CrashAfterObservations_ResumesWithoutCallingTheEvaluatorAgain() =>
        await StepKindScenarios.EvaluateSurvivesACrashAfterItsObservationsWereWritten(Create().Backend);

    [Fact]
    public async Task EmitEvent_CrashAfterTheEvent_WritesItOnceAndResumes() =>
        await StepKindScenarios.EmitEventIsWrittenOnceAcrossACrashAndResume(Create().Backend);

    [Fact]
    public async Task Compensate_CrashBetweenTwoCompensations_RunsOnlyTheRemainingOne() =>
        await StepKindScenarios.CompensationContinuesAfterACrashBetweenTwoCompensations(Create().Backend);

    // ---- WaitForSignal ----------------------------------------------------------------------------------------

    static WorkflowDefinition SignalDefinition(string gateName = "gate") => new("wf", "v1", "Signal",
    [
        new WorkflowStepDefinition(gateName, gateName, []),
        new WorkflowStepDefinition("receipt", "receipt", [gateName], Kind: WorkflowStepKind.WaitForSignal),
        new WorkflowStepDefinition("use", "use", ["receipt"]),
    ]);

    [Fact]
    public async Task SignalAsync_BeforeTheStepIsReady_IsKeptAndTheStepSucceedsWithoutWaiting()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, sink, backend) = Create();
        var runtime = backend.Runtime();
        var request = Request("early", SignalDefinition());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        string? payload = null;
        async Task<DurableWorkflowNodeResult> Handler(DurableWorkflowNodeContext context, CancellationToken token)
        {
            lock (calls) calls.Add(context.Step.StepId);
            if (context.Step.StepId == "gate")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            if (context.Step.StepId == "use") payload = context.Dependencies["receipt"].Signal?.Payload;
            return DurableWorkflowNodeResult.Success();
        }

        var run = runtime.RunAsync(request, Handler, ct);
        await entered.Task.WaitAsync(ct);
        var signalled = await runtime.SignalAsync("early", "receipt", "sent early", "scanner", ct);
        signalled.Nodes["receipt"].Status.ShouldBe(DurableWorkflowNodeStatus.Pending);
        signalled.Nodes["receipt"].Signal.ShouldBe(new DurableWorkflowSignal("receipt", "sent early", "scanner"));
        release.SetResult();
        var finished = await run.WaitAsync(ct);

        finished.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        finished.Nodes["receipt"].Status.ShouldBe(DurableWorkflowNodeStatus.Succeeded);
        payload.ShouldBe("sent early");
        calls.ShouldBe(["gate", "use"]);
        var types = (await backend.AuditAsync("early"));
        types.ShouldNotContain("node.waiting:receipt");
        types.ShouldContain("signal.received:receipt");
        types.ShouldContain("node.succeeded:receipt");
        sink.Events.ShouldNotContain(e => e.EventType == "execution.awaiting_confirmation");
    }

    [Fact]
    public async Task SignalAsync_NothingMatches_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, backend) = Create();
        var runtime = backend.Runtime();
        var request = Request("nomatch", SignalDefinition());
        await runtime.RunAsync(request, Succeeds, ct);

        (await Should.ThrowAsync<InvalidOperationException>(() => runtime.SignalAsync("missing", "receipt", null, "scanner", ct)))
            .Message.ShouldContain("unknown");
        (await Should.ThrowAsync<InvalidOperationException>(() => runtime.SignalAsync("nomatch", "other-name", null, "scanner", ct)))
            .Message.ShouldContain("'other-name'");
        // The gate step is not a WaitForSignal step, so its name matches nothing either.
        (await Should.ThrowAsync<InvalidOperationException>(() => runtime.SignalAsync("nomatch", "gate", null, "scanner", ct)))
            .Message.ShouldContain("'gate'");
        await Should.ThrowAsync<ArgumentException>(() => runtime.SignalAsync("nomatch", " ", null, "scanner", ct));
        await Should.ThrowAsync<ArgumentException>(() => runtime.SignalAsync("nomatch", "receipt", null, "", ct));
        await Should.ThrowAsync<ArgumentException>(() => runtime.SignalAsync("", "receipt", null, "scanner", ct));
    }

    [Fact]
    public async Task SignalAsync_TerminalExecution_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, backend) = Create();
        var runtime = backend.Runtime();
        var request = Request("terminal", SignalDefinition());
        await runtime.RunAsync(request, Succeeds, ct);
        await runtime.SignalAsync("terminal", "receipt", null, "scanner", ct);
        (await runtime.ResumeAsync(request, Succeeds, ct)).Status.ShouldBe(WorkflowExecutionStatus.Succeeded);

        (await Should.ThrowAsync<InvalidOperationException>(() => runtime.SignalAsync("terminal", "receipt", null, "scanner", ct)))
            .Message.ShouldContain("already Succeeded");
    }

    [Fact]
    public async Task SignalAsync_StepAlreadyHoldsASignal_TakesNoSecondOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, backend) = Create();
        var runtime = backend.Runtime();
        var request = Request("twice", SignalDefinition());
        // Nothing has run yet, so the step is pending when the first signal arrives.
        await runtime.RunAsync(request, (context, _) => Task.FromResult(context.Step.StepId == "gate" ? DurableWorkflowNodeResult.Wait("hold") : DurableWorkflowNodeResult.Success()), ct);
        await runtime.SignalAsync("twice", "receipt", "first", "scanner", ct);

        (await Should.ThrowAsync<InvalidOperationException>(() => runtime.SignalAsync("twice", "receipt", "second", "scanner", ct)))
            .Message.ShouldContain("no pending or waiting");
        (await backend.LoadAsync("twice"))!.Nodes["receipt"].Signal!.Payload.ShouldBe("first");
    }

    [Fact]
    public async Task SignalAsync_NameComesFromTheSignalInputOrFallsBackToTheStepId()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, backend) = Create();
        var runtime = backend.Runtime();
        var request = Request("names", new WorkflowDefinition("wf", "v1", "Names",
        [
            new WorkflowStepDefinition("named", "named", [], Inputs(("signal", "go")), Kind: WorkflowStepKind.WaitForSignal),
            new WorkflowStepDefinition("blank", "blank", [], Inputs(("signal", "  ")), Kind: WorkflowStepKind.WaitForSignal),
            new WorkflowStepDefinition("bare", "bare", [], Kind: WorkflowStepKind.WaitForSignal),
        ]));
        var parked = await runtime.RunAsync(request, Succeeds, ct);
        parked.Nodes["named"].Detail.ShouldBe("Step 'named' awaits signal 'go'.");
        parked.Nodes["blank"].Detail.ShouldBe("Step 'blank' awaits signal 'blank'.");

        await Should.ThrowAsync<InvalidOperationException>(() => runtime.SignalAsync("names", "named", null, "scanner", ct));
        await runtime.SignalAsync("names", "go", null, "scanner", ct);
        await runtime.SignalAsync("names", "blank", null, "scanner", ct);
        var all = await runtime.SignalAsync("names", "bare", "last", "scanner", ct);

        all.Nodes.Values.ShouldAllBe(node => node.Status == DurableWorkflowNodeStatus.Succeeded);
        (await runtime.ResumeAsync(request, Succeeds, ct)).Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task SignalAsync_TwoStepsWaitForTheSameName_BothSettleInOneSwap()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, backend) = Create();
        var runtime = backend.Runtime();
        var request = Request("both", new WorkflowDefinition("wf", "v1", "Both",
        [
            new WorkflowStepDefinition("one", "one", [], Inputs(("signal", "go")), Kind: WorkflowStepKind.WaitForSignal),
            new WorkflowStepDefinition("two", "two", [], Inputs(("signal", "go")), Kind: WorkflowStepKind.WaitForSignal),
        ]));
        var parked = await runtime.RunAsync(request, Succeeds, ct);

        var signalled = await runtime.SignalAsync("both", "go", "p", "scanner", ct);

        signalled.Version.ShouldBe(parked.Version + 1);
        signalled.Nodes["one"].CompletionOrder.ShouldBe(1);
        signalled.Nodes["two"].CompletionOrder.ShouldBe(2);
        signalled.Status.ShouldBe(WorkflowExecutionStatus.Running);
        var signalEvent = (await backend.AuditAsync("both")).Single(e => e.StartsWith("signal.received", StringComparison.Ordinal));
        signalEvent.ShouldBe("signal.received");
        (await runtime.ResumeAsync(request, Succeeds, ct)).Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task SignalAsync_LosesTheVersionRace_AppliesTheSignalToTheFreshCheckpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new InMemoryWorkflowStateStore();
        var sink = new InMemoryWorkflowRuntimeAuditSink();
        var request = Request("race-signal", SignalDefinition());
        await new DurableWorkflowRuntime(inner, sink).RunAsync(request, Succeeds, ct);
        var racing = new DurableWorkflowRuntime(new RaceOnceStore(inner, (_, next) => next.Nodes["receipt"].Signal is not null), sink);

        var signalled = await racing.SignalAsync("race-signal", "receipt", "p", "scanner", ct);

        signalled.Nodes["receipt"].Status.ShouldBe(DurableWorkflowNodeStatus.Succeeded);
        (await inner.LoadAsync("race-signal", ct))!.Nodes["receipt"].Signal!.Payload.ShouldBe("p");
    }

    [Fact]
    public async Task EarlySignalAndParking_LoseTheVersionRace_AndStillSettleTheNode()
    {
        var ct = TestContext.Current.CancellationToken;
        // Parking: the first write that waits the receipt loses its race and is redone on the fresh checkpoint.
        var parkInner = new InMemoryWorkflowStateStore();
        var parkSink = new InMemoryWorkflowRuntimeAuditSink();
        var parkRuntime = new DurableWorkflowRuntime(new RaceOnceStore(parkInner, (_, next) => next.Nodes["receipt"].Status == DurableWorkflowNodeStatus.Waiting), parkSink);
        var parked = await parkRuntime.RunAsync(Request("race-park", SignalDefinition()), Succeeds, ct);
        parked.Status.ShouldBe(WorkflowExecutionStatus.AwaitingConfirmation);
        parked.Nodes["receipt"].Status.ShouldBe(DurableWorkflowNodeStatus.Waiting);

        // Settling an early signal: the swap that succeeds the node loses once, then lands.
        var settleInner = new InMemoryWorkflowStateStore();
        var settleSink = new InMemoryWorkflowRuntimeAuditSink();
        var request = Request("race-settle", SignalDefinition());
        var plain = new DurableWorkflowRuntime(settleInner, settleSink);
        var first = await plain.RunAsync(request, (context, _) => Task.FromResult(context.Step.StepId == "gate" ? DurableWorkflowNodeResult.Wait("hold") : DurableWorkflowNodeResult.Success()), ct);
        first.Status.ShouldBe(WorkflowExecutionStatus.AwaitingConfirmation);
        await plain.SignalAsync("race-settle", "receipt", "early", "scanner", ct);
        await plain.DecideAsync("race-settle", "gate", true, "maria", ct: ct);
        var settling = new DurableWorkflowRuntime(new RaceOnceStore(settleInner, (_, next) => next.Nodes["receipt"].Status == DurableWorkflowNodeStatus.Succeeded), settleSink);

        var finished = await settling.ResumeAsync(request, Succeeds, ct);

        finished.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        finished.Nodes["receipt"].Signal!.Payload.ShouldBe("early");
    }

    // ---- Evaluate ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("maximize", 0.8, 0.8, true)]
    [InlineData("maximize", 0.8, 0.79, false)]
    [InlineData("maximize", 0.8, 0.95, true)]
    [InlineData("minimize", 0.2, 0.2, true)]
    [InlineData("minimize", 0.2, 0.21, false)]
    [InlineData("minimize", 0.2, 0.05, true)]
    public async Task Evaluate_AgainstTheObjectiveThreshold_PassesOrFailsByDirection(string direction, double threshold, double value, bool passes)
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, backend) = Create();
        var evaluator = new ScriptedEvaluator(_ => [ScriptedEvaluator.Observe("score", value)]);
        var request = Request("threshold-" + Guid.NewGuid().ToString("N"), StepKindScenarios.EvaluateDefinition(threshold, direction));
        var ran = new List<string>();

        var result = await backend.Runtime(evaluator: evaluator).RunAsync(request, (context, token) =>
        {
            ran.Add(context.Step.StepId);
            return Succeeds(context, token);
        }, ct);

        result.Status.ShouldBe(passes ? WorkflowExecutionStatus.Succeeded : WorkflowExecutionStatus.Failed);
        result.Nodes["eval"].Status.ShouldBe(passes ? DurableWorkflowNodeStatus.Succeeded : DurableWorkflowNodeStatus.Failed);
        result.Nodes["eval"].Observations.ShouldNotBeNull().ShouldHaveSingleItem().Value.ShouldBe(value);
        ran.Contains("after").ShouldBe(passes);
        if (!passes)
        {
            result.FailureReason.ShouldNotBeNull().ShouldContain("'score'");
            result.FailureReason.ShouldContain(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            result.FailureReason.ShouldContain(threshold.ToString(System.Globalization.CultureInfo.InvariantCulture));
            result.FailureReason.ShouldContain(direction);
        }
    }

    [Fact]
    public async Task Evaluate_Miss_WritesOneEvaluatedEventWithEveryMetricBeforeTheFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, backend) = Create();
        var evaluator = new ScriptedEvaluator(_ => [ScriptedEvaluator.Observe("score", 0.1)]);
        var request = Request("miss-audit", StepKindScenarios.EvaluateDefinition());

        await backend.Runtime(evaluator: evaluator).RunAsync(request, Succeeds, ct);

        (await backend.AuditAsync("miss-audit")).ShouldBe(
        [
            "execution.started", "node.started:work", "node.succeeded:work", "node.started:eval", "node.evaluated:eval", "execution.failed",
        ]);
        (await backend.EventsAsync("miss-audit")).Single(e => e.EventType == "node.evaluated").Detail.ShouldBe("score=0.1 (missed maximize threshold 0.8)");
    }

    [Fact]
    public async Task Evaluate_PlanMetricTheEvaluatorDidNotReturn_FailsTheNode()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, backend) = Create();
        var definition = StepKindScenarios.EvaluateDefinition() with { EvaluationPlan = new WorkflowEvaluationPlan(["score", "cost"]) };
        var evaluator = new ScriptedEvaluator(_ => [ScriptedEvaluator.Observe("score", 0.9)]);

        var result = await backend.Runtime(evaluator: evaluator).RunAsync(Request("missing-metric", definition), Succeeds, ct);

        result.Status.ShouldBe(WorkflowExecutionStatus.Failed);
        result.FailureReason.ShouldBe("Metric 'cost' was not returned by the evaluator.");
    }

    [Fact]
    public async Task Evaluate_MetricWithoutThresholdOrObjective_IsRecordedAndPasses()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, sink, backend) = Create();
        var noThreshold = StepKindScenarios.EvaluateDefinition() with
        {
            Objective = new WorkflowObjective([new WorkflowObjectiveMetric("score", "maximize")]),
            EvaluationPlan = new WorkflowEvaluationPlan(["score", "cost"]),
        };
        var evaluator = new ScriptedEvaluator(_ => [ScriptedEvaluator.Observe("score", 0), ScriptedEvaluator.Observe("cost", 5)]);

        var result = await backend.Runtime(evaluator: evaluator).RunAsync(Request("no-threshold", noThreshold), Succeeds, ct);
        result.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        result.Nodes["eval"].Detail.ShouldBe("Evaluated 2 metric(s).");
        sink.Events.Single(e => e.EventType == "node.evaluated").Detail.ShouldBe("score=0; cost=5");
        sink.Events.ShouldNotContain(e => e.EventType == "metric.observed");

        var noObjective = noThreshold with { Objective = null };
        (await backend.Runtime(evaluator: evaluator).RunAsync(Request("no-objective", noObjective), Succeeds, ct)).Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task Evaluate_UnknownDirection_FailsTheNode()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, backend) = Create();
        var evaluator = new ScriptedEvaluator(_ => [ScriptedEvaluator.Observe("score", 1)]);

        var result = await backend.Runtime(evaluator: evaluator).RunAsync(Request("direction", StepKindScenarios.EvaluateDefinition(direction: "sideways")), Succeeds, ct);

        result.Status.ShouldBe(WorkflowExecutionStatus.Failed);
        result.FailureReason.ShouldBe("Metric 'score' has unknown direction 'sideways'.");
    }

    [Fact]
    public async Task Evaluate_EvaluatorThrows_FailsLikeAHandlerExceptionAndIsCalledOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, backend) = Create();
        var calls = 0;
        var evaluator = new ScriptedEvaluator(_ =>
        {
            calls++;
            throw new InvalidOperationException("metrics store is down");
        });

        var result = await backend.Runtime(evaluator: evaluator).RunAsync(Request("thrower", StepKindScenarios.EvaluateDefinition()), Succeeds, ct);

        result.Status.ShouldBe(WorkflowExecutionStatus.Failed);
        result.FailureReason.ShouldBe("metrics store is down");
        result.Nodes["eval"].Observations.ShouldBeNull();
        calls.ShouldBe(1);
    }

    [Fact]
    public async Task Evaluate_CancelledInsideTheEvaluator_EndsCancelled()
    {
        var (_, _, backend) = Create();
        using var cancellation = new CancellationTokenSource();
        var evaluator = new ScriptedEvaluator(_ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });

        var result = await backend.Runtime(evaluator: evaluator).RunAsync(Request("cancelled-eval", StepKindScenarios.EvaluateDefinition()), Succeeds, cancellation.Token);

        result.Status.ShouldBe(WorkflowExecutionStatus.Cancelled);
    }

    [Fact]
    public async Task Evaluate_EvaluatorContext_CarriesPlanMetricsKeyAttemptAndDependencies()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, backend) = Create();
        var evaluator = new ScriptedEvaluator(_ => [ScriptedEvaluator.Observe("score", 1)]);
        var request = Request("context", StepKindScenarios.EvaluateDefinition());

        await backend.Runtime(evaluator: evaluator).RunAsync(request, (context, _) => Task.FromResult(DurableWorkflowNodeResult.Success("work detail")), ct);

        var call = evaluator.Calls.ShouldHaveSingleItem();
        call.MetricIds.ShouldBe(["score"]);
        call.IdempotencyKey.ShouldBe("context:eval");
        call.Attempt.ShouldBe(1);
        call.Step.StepId.ShouldBe("eval");
        call.Dependencies.Keys.ShouldBe(["work"]);
        call.Dependencies["work"].Detail.ShouldBe("work detail");
    }

    [Fact]
    public async Task Evaluate_WithoutAnEnabledPlanOrWithoutAnEvaluator_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, backend) = Create();
        var evaluator = new ScriptedEvaluator(_ => []);
        var definition = StepKindScenarios.EvaluateDefinition();

        (await Should.ThrowAsync<ArgumentException>(() => backend.Runtime(evaluator: evaluator).RunAsync(Request("no-plan", definition with { EvaluationPlan = null }), Succeeds, ct)))
            .Message.ShouldContain("EvaluationPlan");
        (await Should.ThrowAsync<ArgumentException>(() => backend.Runtime(evaluator: evaluator).RunAsync(Request("off-plan", definition with { EvaluationPlan = new WorkflowEvaluationPlan(["score"], Enabled: false) }), Succeeds, ct)))
            .Message.ShouldContain("EvaluationPlan");
        (await Should.ThrowAsync<ArgumentException>(() => backend.Runtime().RunAsync(Request("no-evaluator", definition), Succeeds, ct)))
            .Message.ShouldContain("evaluator");
        evaluator.Calls.ShouldBeEmpty();
    }

    // ---- EmitEvent --------------------------------------------------------------------------------------------

    [Fact]
    public async Task EmitEvent_OnTheNonTransactionalPath_WritesTheEventOnceInANormalRun()
    {
        // InMemoryWorkflowStateStore with a separate sink writes the swap and the event in two steps: one event in a
        // normal run and a resume, but a crash between the two steps would lose it. Only a transactional store avoids that.
        var ct = TestContext.Current.CancellationToken;
        var (_, sink, backend) = Create();
        var request = Request("emit", new WorkflowDefinition("wf", "v1", "Emit",
        [
            new WorkflowStepDefinition("emit", "emit", [], Inputs(("event", "invoice.sent"), ("detail", "n 4")), Kind: WorkflowStepKind.EmitEvent),
            new WorkflowStepDefinition("bare", "bare", [], Inputs(("event", "bare.event")), Kind: WorkflowStepKind.EmitEvent),
        ]));
        var seen = new List<string>();

        var result = await backend.Runtime().RunAsync(request, (context, token) =>
        {
            seen.Add(context.Step.StepId);
            return Succeeds(context, token);
        }, ct);

        result.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        seen.ShouldBeEmpty();
        result.Nodes["emit"].Detail.ShouldBe("n 4");
        result.Nodes["bare"].Detail.ShouldBeNull();
        var events = sink.Events.Where(e => e.EventType.StartsWith("event.", StringComparison.Ordinal)).ToList();
        events.Select(e => (e.EventType, e.StepId, e.Detail)).ShouldBe([("event.invoice.sent", "emit", "n 4"), ("event.bare.event", "bare", null)]);
        (await backend.Runtime().ResumeAsync(request, Succeeds, ct)).Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        sink.Events.Count(e => e.EventType.StartsWith("event.", StringComparison.Ordinal)).ShouldBe(2);
    }

    [Fact]
    public async Task EmitEvent_LosesTheVersionRace_StillWritesTheEventOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new InMemoryWorkflowStateStore();
        var sink = new InMemoryWorkflowRuntimeAuditSink();
        var request = Request("race-emit", new WorkflowDefinition("wf", "v1", "Emit",
            [new WorkflowStepDefinition("emit", "emit", [], Inputs(("event", "x")), Kind: WorkflowStepKind.EmitEvent)]));
        var runtime = new DurableWorkflowRuntime(new RaceOnceStore(inner, (_, next) => next.Nodes["emit"].Status == DurableWorkflowNodeStatus.Succeeded), sink);

        var result = await runtime.RunAsync(request, Succeeds, ct);

        result.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        sink.Events.Count(e => e.EventType == "event.x").ShouldBe(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmitEvent_WithoutANonBlankEventName_IsRejected(string? eventName)
    {
        var (_, _, backend) = Create();
        var inputs = eventName is null ? null : Inputs(("event", eventName));
        var request = Request("bad-event", new WorkflowDefinition("wf", "v1", "Emit",
            [new WorkflowStepDefinition("emit", "emit", [], inputs, Kind: WorkflowStepKind.EmitEvent)]));

        (await Should.ThrowAsync<ArgumentException>(() => backend.Runtime().RunAsync(request, Succeeds, TestContext.Current.CancellationToken)))
            .Message.ShouldContain("'event'");
    }

    [Fact]
    public async Task EmitEvent_InputsWithoutTheEventKey_IsRejected()
    {
        var (_, _, backend) = Create();
        var request = Request("no-event-key", new WorkflowDefinition("wf", "v1", "Emit",
            [new WorkflowStepDefinition("emit", "emit", [], Inputs(("detail", "d")), Kind: WorkflowStepKind.EmitEvent)]));

        await Should.ThrowAsync<ArgumentException>(() => backend.Runtime().RunAsync(request, Succeeds, TestContext.Current.CancellationToken));
    }

    // ---- Compensate -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Compensate_FailureRunsTheCompensationsNewestFirstThenFails()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, backend) = Create();
        var calls = new List<string>();

        var result = await backend.Runtime().RunAsync(Request("reverse", StepKindScenarios.CompensationDefinition()), (context, _) =>
        {
            calls.Add(context.Step.StepId);
            return Task.FromResult(context.Step.StepId == "ship" ? DurableWorkflowNodeResult.Failure("carrier rejected") : DurableWorkflowNodeResult.Success());
        }, ct);

        calls.ShouldBe(["book", "pay", "ship", "undo-pay", "undo-book"]);
        result.Status.ShouldBe(WorkflowExecutionStatus.Failed);
        result.FailureReason.ShouldBe("carrier rejected");
        result.Nodes["book"].CompletionOrder.ShouldBe(1);
        result.Nodes["pay"].CompletionOrder.ShouldBe(2);
    }

    [Fact]
    public async Task Compensate_SuccessfulExecution_SkipsTheCompensationsWithoutRunningThem()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, backend) = Create();
        var calls = new List<string>();

        var result = await backend.Runtime().RunAsync(Request("happy", StepKindScenarios.CompensationDefinition()), (context, token) =>
        {
            calls.Add(context.Step.StepId);
            return Succeeds(context, token);
        }, ct);

        result.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        calls.ShouldBe(["book", "pay", "ship"]);
        result.Nodes["undo-book"].Status.ShouldBe(DurableWorkflowNodeStatus.Skipped);
        result.Nodes["undo-pay"].Status.ShouldBe(DurableWorkflowNodeStatus.Skipped);
        result.Nodes["undo-pay"].Attempts.ShouldBe(0);
    }

    [Fact]
    public async Task Compensate_ACompensationThatFailsIsRecordedAndTheRestStillRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, backend) = Create();
        var calls = new List<string>();

        var result = await backend.Runtime().RunAsync(Request("partial", StepKindScenarios.CompensationDefinition()), (context, _) =>
        {
            calls.Add(context.Step.StepId);
            return Task.FromResult(context.Step.StepId switch
            {
                "ship" => DurableWorkflowNodeResult.Failure("carrier rejected"),
                "undo-pay" => DurableWorkflowNodeResult.Failure("refund declined"),
                _ => DurableWorkflowNodeResult.Success(),
            });
        }, ct);

        calls.ShouldBe(["book", "pay", "ship", "undo-pay", "undo-book"]);
        result.Status.ShouldBe(WorkflowExecutionStatus.Failed);
        result.Nodes["undo-pay"].Status.ShouldBe(DurableWorkflowNodeStatus.Failed);
        result.Nodes["undo-book"].Status.ShouldBe(DurableWorkflowNodeStatus.Succeeded);
        result.FailureReason.ShouldBe("carrier rejected Compensation failed: 'undo-pay' (refund declined).");
    }

    [Fact]
    public async Task Compensate_ARetryableCompensationRetriesLikeAnyStep()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, backend) = Create();
        var undoPayCalls = 0;

        var result = await backend.Runtime().RunAsync(Request("retry-comp", StepKindScenarios.CompensationDefinition()), (context, _) =>
            Task.FromResult(context.Step.StepId switch
            {
                "ship" => DurableWorkflowNodeResult.Failure("carrier rejected"),
                "undo-pay" => ++undoPayCalls == 1 ? DurableWorkflowNodeResult.Failure("busy", retryable: true) : DurableWorkflowNodeResult.Success(),
                _ => DurableWorkflowNodeResult.Success(),
            }), ct);

        undoPayCalls.ShouldBe(2);
        result.Nodes["undo-pay"].Attempts.ShouldBe(2);
        result.Nodes["undo-pay"].Status.ShouldBe(DurableWorkflowNodeStatus.Succeeded);
        result.FailureReason.ShouldBe("carrier rejected");
        (await backend.AuditAsync("retry-comp")).ShouldContain("node.retrying:undo-pay");
    }

    [Fact]
    public async Task Compensate_AFailedCompensationIsNeverTheCauseAfterACrash()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, backend) = Create();
        var request = Request("failed-comp-crash", StepKindScenarios.CompensationDefinition());
        Task<DurableWorkflowNodeResult> Handler(DurableWorkflowNodeContext context, CancellationToken _) =>
            Task.FromResult(context.Step.StepId switch
            {
                "ship" => DurableWorkflowNodeResult.Failure("carrier rejected"),
                "undo-pay" => DurableWorkflowNodeResult.Failure("refund declined"),
                _ => DurableWorkflowNodeResult.Success(),
            });

        await Should.ThrowAsync<SimulatedCrashException>(() =>
            backend.Runtime(StepKindBackend.After("node.failed", "undo-pay")).RunAsync(request, Handler, ct));
        var resumed = await backend.Runtime().ResumeAsync(request, Handler, ct);

        resumed.Status.ShouldBe(WorkflowExecutionStatus.Failed);
        resumed.FailureReason.ShouldBe("carrier rejected Compensation failed: 'undo-pay' (refund declined).");
        resumed.Nodes["undo-book"].Status.ShouldBe(DurableWorkflowNodeStatus.Succeeded);
    }

    [Fact]
    public async Task Compensate_ACompensationIsNotRunForAStepThatFailedItself()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, backend) = Create();
        var calls = new List<string>();
        var request = Request("self-failed", new WorkflowDefinition("wf", "v1", "Saga",
        [
            new WorkflowStepDefinition("charge", "charge", [], CompensationStepId: "refund"),
            new WorkflowStepDefinition("refund", "refund", [], Kind: WorkflowStepKind.Compensate),
        ]));

        var result = await backend.Runtime().RunAsync(request, (context, _) =>
        {
            calls.Add(context.Step.StepId);
            return Task.FromResult(DurableWorkflowNodeResult.Failure("declined"));
        }, ct);

        result.Status.ShouldBe(WorkflowExecutionStatus.Failed);
        calls.ShouldBe(["charge"]);
        result.Nodes["refund"].Status.ShouldBe(DurableWorkflowNodeStatus.Pending);
    }

    [Fact]
    public async Task Compensate_TwoStepsNamingOneCompensation_RunItOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, backend) = Create();
        var calls = new List<string>();
        var request = Request("shared", new WorkflowDefinition("wf", "v1", "Saga",
        [
            new WorkflowStepDefinition("undo", "undo", [], Kind: WorkflowStepKind.Compensate),
            new WorkflowStepDefinition("one", "one", [], CompensationStepId: "undo"),
            new WorkflowStepDefinition("two", "two", ["one"], CompensationStepId: "undo"),
            new WorkflowStepDefinition("boom", "boom", ["two"]),
        ]));

        await backend.Runtime().RunAsync(request, (context, _) =>
        {
            calls.Add(context.Step.StepId);
            return Task.FromResult(context.Step.StepId == "boom" ? DurableWorkflowNodeResult.Failure("no") : DurableWorkflowNodeResult.Success());
        }, ct);

        calls.ShouldBe(["one", "two", "boom", "undo"]);
    }

    [Fact]
    public async Task Compensate_CancellationRequestedWhileTheStepFails_DoesNotCompensate()
    {
        var (_, _, backend) = Create();
        using var cancellation = new CancellationTokenSource();
        var calls = new List<string>();

        var result = await backend.Runtime().RunAsync(Request("cancel-forward", StepKindScenarios.CompensationDefinition()), (context, _) =>
        {
            calls.Add(context.Step.StepId);
            if (context.Step.StepId != "ship") return Task.FromResult(DurableWorkflowNodeResult.Success());
            cancellation.Cancel();
            return Task.FromResult(DurableWorkflowNodeResult.Failure("carrier rejected"));
        }, cancellation.Token);

        result.Status.ShouldBe(WorkflowExecutionStatus.Cancelled);
        calls.ShouldBe(["book", "pay", "ship"]);
    }

    [Fact]
    public async Task Compensate_CancellationRequestedAfterTheFailureWasRecorded_DoesNotCompensate()
    {
        var store = new InMemoryWorkflowStateStore();
        var sink = new InMemoryWorkflowRuntimeAuditSink();
        using var cancellation = new CancellationTokenSource();
        var runtime = new DurableWorkflowRuntime(store, new HookedAuditSink(sink, "node.failed", cancellation.Cancel));
        var calls = new List<string>();

        var result = await runtime.RunAsync(Request("cancel-recorded", StepKindScenarios.CompensationDefinition()), (context, _) =>
        {
            calls.Add(context.Step.StepId);
            return Task.FromResult(context.Step.StepId == "ship" ? DurableWorkflowNodeResult.Failure("carrier rejected") : DurableWorkflowNodeResult.Success());
        }, cancellation.Token);

        result.Status.ShouldBe(WorkflowExecutionStatus.Cancelled);
        calls.ShouldBe(["book", "pay", "ship"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Compensate_CancelledWhileCompensating_StopsWithoutRunningTheRest(bool throws)
    {
        var (_, _, backend) = Create();
        using var cancellation = new CancellationTokenSource();
        var calls = new List<string>();

        var result = await backend.Runtime().RunAsync(Request("cancel-comp-" + throws, StepKindScenarios.CompensationDefinition()), (context, token) =>
        {
            calls.Add(context.Step.StepId);
            if (context.Step.StepId == "ship") return Task.FromResult(DurableWorkflowNodeResult.Failure("carrier rejected"));
            if (context.Step.StepId != "undo-pay") return Task.FromResult(DurableWorkflowNodeResult.Success());
            cancellation.Cancel();
            return throws ? throw new OperationCanceledException(token) : Task.FromResult(DurableWorkflowNodeResult.Success());
        }, cancellation.Token);

        result.Status.ShouldBe(WorkflowExecutionStatus.Cancelled);
        calls.ShouldBe(["book", "pay", "ship", "undo-pay"]);
    }

    [Fact]
    public async Task Compensate_RejectedApprovalWithCompensationsOwed_StaysOpenUntilResumeCompensatesThenFails()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, backend) = Create();
        var runtime = backend.Runtime();
        var request = Request("reject-comp", new WorkflowDefinition("wf", "v1", "Saga",
        [
            new WorkflowStepDefinition("book", "book", [], CompensationStepId: "undo-book"),
            new WorkflowStepDefinition("undo-book", "undo-book", [], Kind: WorkflowStepKind.Compensate),
            new WorkflowStepDefinition("approve", "approve", ["book"], Kind: WorkflowStepKind.Approval),
        ]));
        var calls = new List<string>();
        Task<DurableWorkflowNodeResult> Handler(DurableWorkflowNodeContext context, CancellationToken token)
        {
            calls.Add(context.Step.StepId);
            return Succeeds(context, token);
        }
        await runtime.RunAsync(request, Handler, ct);

        var rejected = await runtime.DecideAsync("reject-comp", "approve", approved: false, "maria", "too risky", ct);

        rejected.Status.ShouldBe(WorkflowExecutionStatus.Running);
        // The reason is kept so nothing else starts; the execution fails only after the compensations ran.
        rejected.FailureReason.ShouldBe("too risky");
        rejected.Nodes["approve"].Status.ShouldBe(DurableWorkflowNodeStatus.Failed);
        calls.ShouldBe(["book"]);

        var finished = await runtime.ResumeAsync(request, Handler, ct);

        finished.Status.ShouldBe(WorkflowExecutionStatus.Failed);
        finished.FailureReason.ShouldBe("too risky");
        finished.Nodes["undo-book"].Status.ShouldBe(DurableWorkflowNodeStatus.Succeeded);
        calls.ShouldBe(["book", "undo-book"]);
        (await backend.AuditAsync("reject-comp")).TakeLast(4).ShouldBe(["node.rejected:approve", "node.started:undo-book", "node.succeeded:undo-book", "execution.failed"]);
    }

    [Fact]
    public async Task Compensate_RejectionKeptOpen_StepMadeReadyAfterwardsNeverStartsAndCompensationsRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, backend) = Create();
        var runtime = backend.Runtime();
        var request = Request("reject-then-ready", new WorkflowDefinition("wf", "v1", "Saga",
        [
            new WorkflowStepDefinition("book", "book", [], CompensationStepId: "undo-book"),
            new WorkflowStepDefinition("undo-book", "undo-book", [], Kind: WorkflowStepKind.Compensate),
            new WorkflowStepDefinition("gate-a", "gate-a", ["book"], Kind: WorkflowStepKind.Approval),
            new WorkflowStepDefinition("gate-b", "gate-b", [], Kind: WorkflowStepKind.Approval),
            new WorkflowStepDefinition("after-b", "after-b", ["gate-b"]),
        ]));
        var calls = new List<string>();
        Task<DurableWorkflowNodeResult> Handler(DurableWorkflowNodeContext context, CancellationToken token)
        {
            calls.Add(context.Step.StepId);
            return Succeeds(context, token);
        }
        await runtime.RunAsync(request, Handler, ct);
        await runtime.DecideAsync("reject-then-ready", "gate-a", approved: false, "maria", "too risky", ct);
        // The second gate is approved after the rejection, which makes after-b ready while the execution is still open.
        var approvedLater = await runtime.DecideAsync("reject-then-ready", "gate-b", approved: true, "sam", "ok", ct);
        approvedLater.Status.ShouldBe(WorkflowExecutionStatus.Running);

        var finished = await runtime.ResumeAsync(request, Handler, ct);

        finished.Status.ShouldBe(WorkflowExecutionStatus.Failed);
        finished.FailureReason.ShouldBe("too risky");
        calls.ShouldBe(["book", "undo-book"]);
        finished.Nodes["after-b"].Status.ShouldBe(DurableWorkflowNodeStatus.Pending);
        finished.Nodes["undo-book"].Status.ShouldBe(DurableWorkflowNodeStatus.Succeeded);
    }

    [Fact]
    public async Task Compensate_RejectedApprovalWithCompensationAlreadyDone_FailsAtOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, backend) = Create();
        var runtime = backend.Runtime();
        var request = Request("reject-done", new WorkflowDefinition("wf", "v1", "Saga",
        [
            new WorkflowStepDefinition("book", "book", [], CompensationStepId: "undo-book"),
            new WorkflowStepDefinition("undo-book", "undo-book", [], Kind: WorkflowStepKind.Compensate),
            new WorkflowStepDefinition("approve", "approve", ["book"], Kind: WorkflowStepKind.Approval),
        ]));
        await runtime.RunAsync(request, Succeeds, ct);
        var stored = (await backend.LoadAsync("reject-done"))!;
        // The compensation already ran before this decision arrives.
        var nodes = stored.Nodes.ToDictionary(pair => pair.Key, pair => pair.Value);
        nodes["undo-book"] = nodes["undo-book"] with { Status = DurableWorkflowNodeStatus.Succeeded };
        (await backend.Store.TryUpdateAsync("reject-done", stored, stored with { Nodes = nodes, Version = stored.Version + 1 }, ct)).ShouldBeTrue();

        var rejected = await runtime.DecideAsync("reject-done", "approve", approved: false, "maria", "no", ct);

        rejected.Status.ShouldBe(WorkflowExecutionStatus.Failed);
    }

    [Fact]
    public async Task Run_ASiblingFailsInTheSameBatch_NoLaterStepStarts()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, backend) = Create();
        var started = new List<string>();
        var request = Request("failfast", new WorkflowDefinition("wf", "v1", "Parallel",
        [
            new WorkflowStepDefinition("bad", "bad", []),
            new WorkflowStepDefinition("good", "good", []),
            new WorkflowStepDefinition("later", "later", ["good"]),
        ]));

        var result = await backend.Runtime().RunAsync(request, (context, _) =>
        {
            lock (started) started.Add(context.Step.StepId);
            return Task.FromResult(context.Step.StepId == "bad" ? DurableWorkflowNodeResult.Failure("broke") : DurableWorkflowNodeResult.Success());
        }, ct);

        result.Status.ShouldBe(WorkflowExecutionStatus.Failed);
        started.ShouldNotContain("later");
    }

    // ---- shared paths touched by the new kinds ----------------------------------------------------------------

    [Fact]
    public async Task Run_HandlerThrows_FailsTheNodeWithTheMessageAndDoesNotRetry()
    {
        var (_, _, backend) = Create();
        var calls = 0;

        var result = await backend.Runtime().RunAsync(Request("thrower", new WorkflowDefinition("wf", "v1", "T", [new WorkflowStepDefinition("a", "a", [])])), (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("handler broke");
        }, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(WorkflowExecutionStatus.Failed);
        result.FailureReason.ShouldBe("handler broke");
        calls.ShouldBe(1);
    }

    [Fact]
    public void NodeContext_HasValueEqualityAndEmptyDependenciesByDefault()
    {
        var request = Request("ctx", new WorkflowDefinition("wf", "v1", "T", [new WorkflowStepDefinition("a", "a", [])]));
        var step = request.Workflow.Steps[0];
        var first = new DurableWorkflowNodeContext(request, step, "k", 1);
        var second = first with { };

        second.ShouldBe(first);
        second.GetHashCode().ShouldBe(first.GetHashCode());
        first.ToString().ShouldContain("IdempotencyKey = k");
        first.Dependencies.ShouldBeEmpty();
        (first with { Dependencies = new Dictionary<string, DurableWorkflowNodeSnapshot>() }).Dependencies.ShouldNotBeNull();
    }

    [Theory]
    [InlineData(WorkflowExecutionStatus.Succeeded)]
    [InlineData(WorkflowExecutionStatus.Failed)]
    public async Task Run_TheFinalSwapLosesTheVersionRace_IsAppliedToTheFreshCheckpoint(WorkflowExecutionStatus final)
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new InMemoryWorkflowStateStore();
        var runtime = new DurableWorkflowRuntime(new RaceOnceStore(inner, (_, next) => next.Status == final), new InMemoryWorkflowRuntimeAuditSink());
        var request = Request("final-race-" + final, new WorkflowDefinition("wf", "v1", "T", [new WorkflowStepDefinition("a", "a", [])]));

        var result = await runtime.RunAsync(request, (_, _) => Task.FromResult(final == WorkflowExecutionStatus.Succeeded ? DurableWorkflowNodeResult.Success() : DurableWorkflowNodeResult.Failure("no")), ct);

        result.Status.ShouldBe(final);
        (await inner.LoadAsync(request.ExecutionId, ct))!.Status.ShouldBe(final);
    }

    [Fact]
    public async Task DecideAsync_LosesTheVersionRace_AppliesTheDecisionToTheFreshCheckpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new InMemoryWorkflowStateStore();
        var sink = new InMemoryWorkflowRuntimeAuditSink();
        var request = ApprovalPauseResumeTests.ApprovalRequest("decide-race");
        await new DurableWorkflowRuntime(inner, sink).RunAsync(request, ApprovalPauseResumeTests.Record([]), ct);
        var racing = new DurableWorkflowRuntime(new RaceOnceStore(inner, (_, next) => next.Nodes["approve"].Decision is not null), sink);

        var decided = await racing.DecideAsync("decide-race", "approve", true, "maria", ct: ct);

        decided.Nodes["approve"].Status.ShouldBe(DurableWorkflowNodeStatus.Succeeded);
        decided.Nodes["approve"].CompletionOrder.ShouldBeGreaterThan(0);
    }

    // ---- interleavings ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Signal_LandsBetweenTheDecisionToParkAndThePark_SettlesTheStepInsteadOfParkingIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new InMemoryWorkflowStateStore();
        var sink = new InMemoryWorkflowRuntimeAuditSink();
        var request = Request("lost-signal", new WorkflowDefinition("wf", "v1", "Signal",
            [new WorkflowStepDefinition("receipt", "receipt", [], Inputs(("signal", "rc")), Kind: WorkflowStepKind.WaitForSignal)]));
        var sender = new DurableWorkflowRuntime(inner, sink);
        // The run has seen the step Pending and is about to park it when another process delivers the signal.
        var racing = new DurableWorkflowRuntime(new BeforeUpdateStore(inner, next => next.Nodes["receipt"].Status == DurableWorkflowNodeStatus.Waiting,
            () => sender.SignalAsync("lost-signal", "rc", "early", "scanner", ct)), sink);

        var result = await racing.RunAsync(request, Succeeds, ct);

        result.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        result.Nodes["receipt"].Status.ShouldBe(DurableWorkflowNodeStatus.Succeeded);
        result.Nodes["receipt"].Signal.ShouldBe(new DurableWorkflowSignal("rc", "early", "scanner"));
        sink.Events.ShouldNotContain(e => e.EventType == "node.waiting");
    }

    [Fact]
    public async Task Compensate_SiblingSucceedsInTheBatchOfTheFailingStep_IsCompensatedToo()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, backend) = Create();
        var calls = new List<string>();
        var request = Request("sibling", new WorkflowDefinition("wf", "v1", "Saga",
        [
            new WorkflowStepDefinition("undo", "undo", [], Kind: WorkflowStepKind.Compensate),
            new WorkflowStepDefinition("bad", "bad", []),
            new WorkflowStepDefinition("good", "good", [], CompensationStepId: "undo"),
        ]));

        var result = await backend.Runtime().RunAsync(request, (context, _) =>
        {
            lock (calls) calls.Add(context.Step.StepId);
            return Task.FromResult(context.Step.StepId == "bad" ? DurableWorkflowNodeResult.Failure("boom") : DurableWorkflowNodeResult.Success());
        }, ct);

        result.Status.ShouldBe(WorkflowExecutionStatus.Failed);
        result.FailureReason.ShouldBe("boom");
        calls.Last().ShouldBe("undo");
        calls.Count.ShouldBe(3);
        result.Nodes["good"].Status.ShouldBe(DurableWorkflowNodeStatus.Succeeded);
        result.Nodes["undo"].Status.ShouldBe(DurableWorkflowNodeStatus.Succeeded);
        (await backend.AuditAsync("sibling")).TakeLast(5).ShouldBe(["node.failed:bad", "node.succeeded:good", "node.started:undo", "node.succeeded:undo", "execution.failed"]);
    }

    [Fact]
    public async Task Evaluate_ObservedMetricOutsideThePlanThatTheObjectiveBounds_FailsTheNode()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, backend) = Create();
        var definition = new WorkflowDefinition("wf", "v1", "E",
            [new WorkflowStepDefinition("eval", "eval", [], Kind: WorkflowStepKind.Evaluate)],
            Objective: new WorkflowObjective([new WorkflowObjectiveMetric("a", "maximize", 0.5), new WorkflowObjectiveMetric("b", "maximize", 0.9)]),
            EvaluationPlan: new WorkflowEvaluationPlan(["a"]));
        var evaluator = new ScriptedEvaluator(_ => [ScriptedEvaluator.Observe("a", 0.6), ScriptedEvaluator.Observe("b", 0.1)]);

        var result = await backend.Runtime(evaluator: evaluator).RunAsync(Request("outside-plan", definition), Succeeds, ct);

        result.Status.ShouldBe(WorkflowExecutionStatus.Failed);
        result.FailureReason.ShouldBe("Metric 'b' value 0.1 misses threshold 0.9 (maximize).");
        (await backend.EventsAsync("outside-plan")).Single(e => e.EventType == "node.evaluated").Detail
            .ShouldBe("a=0.6 (met maximize threshold 0.5); b=0.1 (missed maximize threshold 0.9)");
    }

    // ---- validation -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Validation_CompensationTargetMissingOfAnotherKindOrUnnamed_IsRejected()
    {
        var (_, _, backend) = Create();
        var ct = TestContext.Current.CancellationToken;

        var missing = Request("v-missing", new WorkflowDefinition("wf", "v1", "V", [new WorkflowStepDefinition("a", "a", [], CompensationStepId: "nope")]));
        (await Should.ThrowAsync<ArgumentException>(() => backend.Runtime().RunAsync(missing, Succeeds, ct))).Message.ShouldContain("does not exist");

        var wrongKind = Request("v-kind", new WorkflowDefinition("wf", "v1", "V",
            [new WorkflowStepDefinition("a", "a", [], CompensationStepId: "b"), new WorkflowStepDefinition("b", "b", [])]));
        (await Should.ThrowAsync<ArgumentException>(() => backend.Runtime().RunAsync(wrongKind, Succeeds, ct))).Message.ShouldContain("not Compensate");

        var unnamed = Request("v-unnamed", new WorkflowDefinition("wf", "v1", "V",
            [new WorkflowStepDefinition("a", "a", []), new WorkflowStepDefinition("undo", "undo", [], Kind: WorkflowStepKind.Compensate)]));
        (await Should.ThrowAsync<ArgumentException>(() => backend.Runtime().RunAsync(unnamed, Succeeds, ct))).Message.ShouldContain("not named by any step");
    }

    [Fact]
    public async Task Run_StepsThatDependOnEachOther_FailAsUnresolved()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, backend) = Create();
        var request = Request("unresolved", new WorkflowDefinition("wf", "v1", "V",
        [
            new WorkflowStepDefinition("a", "a", ["missing-dep-holder"]),
            new WorkflowStepDefinition("missing-dep-holder", "m", ["a"]),
        ]));

        var result = await backend.Runtime().RunAsync(request, Succeeds, ct);

        result.Status.ShouldBe(WorkflowExecutionStatus.Failed);
        result.FailureReason.ShouldBe("Workflow contains unresolved dependencies.");
    }
}

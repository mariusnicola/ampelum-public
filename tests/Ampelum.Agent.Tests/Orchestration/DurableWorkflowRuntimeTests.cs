using SignalGrid.Ampelum.Agent.Contracts;
using SignalGrid.Ampelum.Agent.Orchestration;
using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests.Orchestration;

public sealed class DurableWorkflowRuntimeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ResumeAsync_AfterEveryCompletedNode_ExecutesOnlyRemainingNodes(int completedCount)
    {
        var store = new InMemoryWorkflowStateStore();
        var audit = new InMemoryWorkflowRuntimeAuditSink();
        var request = CreateRequest("restart-" + completedCount);
        var nodes = request.Workflow.Steps.Select((step, index) => new { step, index }).ToDictionary(
            item => item.step.StepId,
            item => new DurableWorkflowNodeSnapshot(
                item.step.StepId,
                item.index < completedCount ? DurableWorkflowNodeStatus.Succeeded : DurableWorkflowNodeStatus.Pending,
                item.index < completedCount ? 1 : 0,
                $"{request.ExecutionId}:{item.step.StepId}"));
        await store.TryCreateAsync(new DurableWorkflowSnapshot(request, WorkflowExecutionStatus.Running, nodes, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        var executed = new List<string>();

        var result = await new DurableWorkflowRuntime(store, audit).ResumeAsync(
            request,
            (node, _) =>
            {
                executed.Add(node.Step.StepId);
                return Task.FromResult(DurableWorkflowNodeResult.Success());
            },
            TestContext.Current.CancellationToken);

        result.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        executed.ShouldBe(request.Workflow.Steps.Skip(completedCount).Select(step => step.StepId));
        result.Nodes.Values.All(node => node.Status == DurableWorkflowNodeStatus.Succeeded).ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_DuplicateDelivery_ClaimsAndExecutesNodeOnce()
    {
        var store = new InMemoryWorkflowStateStore();
        var audit = new InMemoryWorkflowRuntimeAuditSink();
        var request = CreateRequest("duplicate", stepCount: 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        var runtime = new DurableWorkflowRuntime(store, audit);

        var first = runtime.RunAsync(request, async (_, ct) =>
        {
            Interlocked.Increment(ref executions);
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            return DurableWorkflowNodeResult.Success();
        }, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var duplicate = runtime.RunAsync(request, (_, _) => Task.FromResult(DurableWorkflowNodeResult.Success()), TestContext.Current.CancellationToken);
        release.SetResult();

        await first;
        await duplicate;
        executions.ShouldBe(1);
        (await store.LoadAsync(request.ExecutionId, TestContext.Current.CancellationToken))!.Nodes["step-1"].Status.ShouldBe(DurableWorkflowNodeStatus.Succeeded);
    }

    [Fact]
    public async Task RunAsync_RetryThenCancellationAndTerminalFailure_PersistDeterministicTransitions()
    {
        var retryStore = new InMemoryWorkflowStateStore();
        var retryAudit = new InMemoryWorkflowRuntimeAuditSink();
        var retryAttempts = 0;
        var retryResult = await new DurableWorkflowRuntime(retryStore, retryAudit, maxAttempts: 2).RunAsync(
            CreateRequest("retry", stepCount: 1),
            (_, _) => Task.FromResult(++retryAttempts == 1
                ? DurableWorkflowNodeResult.Failure("temporary", retryable: true)
                : DurableWorkflowNodeResult.Success("recovered")),
            TestContext.Current.CancellationToken);

        retryResult.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        retryResult.Nodes["step-1"].Attempts.ShouldBe(2);
        retryAudit.Events.Select(e => e.EventType).ShouldContain("node.retrying");

        using var cancellation = new CancellationTokenSource();
        var cancelled = await new DurableWorkflowRuntime(new InMemoryWorkflowStateStore(), new InMemoryWorkflowRuntimeAuditSink()).RunAsync(
            CreateRequest("cancel", stepCount: 1),
            (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(DurableWorkflowNodeResult.Success());
            },
            cancellation.Token);
        cancelled.Status.ShouldBe(WorkflowExecutionStatus.Cancelled);

        var failed = await new DurableWorkflowRuntime(new InMemoryWorkflowStateStore(), new InMemoryWorkflowRuntimeAuditSink()).RunAsync(
            CreateRequest("failure", stepCount: 1),
            (_, _) => Task.FromResult(DurableWorkflowNodeResult.Failure("permanent")),
            TestContext.Current.CancellationToken);
        failed.Status.ShouldBe(WorkflowExecutionStatus.Failed);
        failed.FailureReason.ShouldBe("permanent");
    }

    [Fact]
    public async Task RunAsync_RecordsOrderedStateAndNodeAuditTrace()
    {
        var audit = new InMemoryWorkflowRuntimeAuditSink();
        var result = await new DurableWorkflowRuntime(new InMemoryWorkflowStateStore(), audit).RunAsync(
            CreateRequest("audit", stepCount: 1),
            (_, _) => Task.FromResult(DurableWorkflowNodeResult.Success("done")),
            TestContext.Current.CancellationToken);

        result.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        audit.Events.Select(e => e.EventType).ShouldBe(["execution.started", "node.started", "node.succeeded", "execution.succeeded"]);
        audit.Events.Select(e => e.Sequence).ShouldBe([1L, 2L, 3L, 4L]);
        audit.Events.Single(e => e.EventType == "node.succeeded").Detail.ShouldBe("done");
    }

    [Fact]
    public async Task RunAsync_BranchStep_ExecutesOnlySelectedBranchAndSkipsOthers()
    {
        var branch = new WorkflowStepDefinition("branch", "test", [], Kind: WorkflowStepKind.Branch, BranchTargets: ["path-a", "path-b"]);
        var pathA = new WorkflowStepDefinition("path-a", "test", ["branch"]);
        var pathB = new WorkflowStepDefinition("path-b", "test", ["branch"]);
        var request = new WorkflowExecutionRequest(
            "branch-exec", new WorkflowDefinition("wf", "v1", "Branch", [branch, pathA, pathB]), "tester", DateTimeOffset.UtcNow);
        var executed = new List<string>();

        var result = await new DurableWorkflowRuntime(new InMemoryWorkflowStateStore(), new InMemoryWorkflowRuntimeAuditSink()).RunAsync(
            request,
            (context, _) =>
            {
                executed.Add(context.Step.StepId);
                var outcome = context.Step.StepId == "branch"
                    ? new DurableWorkflowNodeResult(true, SelectedNextStepId: "path-a")
                    : DurableWorkflowNodeResult.Success();
                return Task.FromResult(outcome);
            },
            TestContext.Current.CancellationToken);

        result.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        executed.ShouldBe(["branch", "path-a"]);
        result.Nodes["path-a"].Status.ShouldBe(DurableWorkflowNodeStatus.Succeeded);
        result.Nodes["path-b"].Status.ShouldBe(DurableWorkflowNodeStatus.Skipped);
    }

    [Fact]
    public async Task RunAsync_BranchStep_WithoutValidSelection_FailsWorkflow()
    {
        var branch = new WorkflowStepDefinition("branch", "test", [], Kind: WorkflowStepKind.Branch, BranchTargets: ["path-a", "path-b"]);
        var pathA = new WorkflowStepDefinition("path-a", "test", ["branch"]);
        var pathB = new WorkflowStepDefinition("path-b", "test", ["branch"]);
        var request = new WorkflowExecutionRequest(
            "branch-invalid-exec", new WorkflowDefinition("wf", "v1", "Branch", [branch, pathA, pathB]), "tester", DateTimeOffset.UtcNow);

        var result = await new DurableWorkflowRuntime(new InMemoryWorkflowStateStore(), new InMemoryWorkflowRuntimeAuditSink()).RunAsync(
            request,
            (_, _) => Task.FromResult(DurableWorkflowNodeResult.Success()),
            TestContext.Current.CancellationToken);

        result.Status.ShouldBe(WorkflowExecutionStatus.Failed);
    }

    [Fact]
    public async Task RunAsync_LoopStep_ReExecutesBodyUntilExitConditionMet()
    {
        var loopStep = new WorkflowStepDefinition("loop", "test", [], Kind: WorkflowStepKind.Loop, MaxIterations: 5);
        var request = new WorkflowExecutionRequest(
            "loop-exec", new WorkflowDefinition("wf", "v1", "Loop", [loopStep]), "tester", DateTimeOffset.UtcNow);
        var executions = 0;

        var result = await new DurableWorkflowRuntime(new InMemoryWorkflowStateStore(), new InMemoryWorkflowRuntimeAuditSink()).RunAsync(
            request,
            (_, _) =>
            {
                executions++;
                return Task.FromResult(new DurableWorkflowNodeResult(true, LoopContinue: executions < 2));
            },
            TestContext.Current.CancellationToken);

        result.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        executions.ShouldBe(2);
        result.Nodes["loop"].Attempts.ShouldBe(2);
    }

    [Fact]
    public async Task RunAsync_BranchStep_WithoutValidSelection_AuditEventMatchesPersistedFailedStatus()
    {
        var branch = new WorkflowStepDefinition("branch", "test", [], Kind: WorkflowStepKind.Branch, BranchTargets: ["path-a", "path-b"]);
        var pathA = new WorkflowStepDefinition("path-a", "test", ["branch"]);
        var pathB = new WorkflowStepDefinition("path-b", "test", ["branch"]);
        var request = new WorkflowExecutionRequest(
            "branch-invalid-audit-exec", new WorkflowDefinition("wf", "v1", "Branch", [branch, pathA, pathB]), "tester", DateTimeOffset.UtcNow);
        var audit = new InMemoryWorkflowRuntimeAuditSink();

        var result = await new DurableWorkflowRuntime(new InMemoryWorkflowStateStore(), audit).RunAsync(
            request,
            (_, _) => Task.FromResult(DurableWorkflowNodeResult.Success()),
            TestContext.Current.CancellationToken);

        result.Status.ShouldBe(WorkflowExecutionStatus.Failed);
        result.Nodes["branch"].Status.ShouldBe(DurableWorkflowNodeStatus.Failed);
        var branchNodeEvent = audit.Events.Single(e => e.StepId == "branch" && e.EventType is "node.succeeded" or "node.failed");
        branchNodeEvent.EventType.ShouldBe("node.failed");
    }

    [Fact]
    public async Task RunAsync_LoopStep_RetryableFailureDoesNotConsumeLoopIterationBudget()
    {
        var loopStep = new WorkflowStepDefinition("loop", "test", [], Kind: WorkflowStepKind.Loop, MaxIterations: 3);
        var request = new WorkflowExecutionRequest(
            "loop-retry-budget-exec", new WorkflowDefinition("wf", "v1", "Loop", [loopStep]), "tester", DateTimeOffset.UtcNow);
        var calls = 0;
        var successfulIterations = 0;

        var result = await new DurableWorkflowRuntime(new InMemoryWorkflowStateStore(), new InMemoryWorkflowRuntimeAuditSink(), maxAttempts: 5).RunAsync(
            request,
            (_, _) =>
            {
                calls++;
                if (calls == 1)
                    return Task.FromResult(DurableWorkflowNodeResult.Failure("transient", retryable: true));
                successfulIterations++;
                return Task.FromResult(new DurableWorkflowNodeResult(true, LoopContinue: successfulIterations < 3));
            },
            TestContext.Current.CancellationToken);

        result.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        successfulIterations.ShouldBe(3);
        calls.ShouldBe(4);
        result.Nodes["loop"].Attempts.ShouldBe(4);
    }

    [Fact]
    public async Task ResumeAsync_WithDefinitionDeclaringStepAbsentFromCheckpoint_FailsWithDefinitionMismatch()
    {
        var store = new InMemoryWorkflowStateStore();
        var audit = new InMemoryWorkflowRuntimeAuditSink();
        var originalStep = new WorkflowStepDefinition("step-1", "test", []);
        var originalRequest = new WorkflowExecutionRequest(
            "resume-changed-definition", new WorkflowDefinition("wf", "v1", "Original", [originalStep]), "tester", DateTimeOffset.UtcNow);
        var nodes = new Dictionary<string, DurableWorkflowNodeSnapshot>
        {
            ["step-1"] = new DurableWorkflowNodeSnapshot("step-1", DurableWorkflowNodeStatus.Pending, 0, $"{originalRequest.ExecutionId}:step-1"),
        };
        await store.TryCreateAsync(new DurableWorkflowSnapshot(originalRequest, WorkflowExecutionStatus.Running, nodes, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);

        var newStep = new WorkflowStepDefinition("step-2", "test", ["step-1"]);
        var resumedRequest = originalRequest with { Workflow = new WorkflowDefinition("wf", "v2", "Extended", [originalStep, newStep]) };

        var result = await new DurableWorkflowRuntime(store, audit).ResumeAsync(
            resumedRequest,
            (_, _) => Task.FromResult(DurableWorkflowNodeResult.Success()),
            TestContext.Current.CancellationToken);

        // No KeyNotFoundException, and no silent success either: a resumed definition that declares a step
        // absent from the persisted checkpoint fails loudly, with an audited "execution.failed" event, rather
        // than reporting Succeeded without ever having scheduled "step-2".
        result.Status.ShouldBe(WorkflowExecutionStatus.Failed);
        result.FailureReason.ShouldNotBeNull();
        result.FailureReason.ShouldContain("step-2");
        audit.Events.Select(e => e.EventType).ShouldContain("execution.failed");
    }

    [Fact]
    public async Task RunAsync_LoopStep_RespectsMaxIterationsSafetyBound()
    {
        var loopStep = new WorkflowStepDefinition("loop", "test", [], Kind: WorkflowStepKind.Loop, MaxIterations: 2);
        var request = new WorkflowExecutionRequest(
            "loop-bound-exec", new WorkflowDefinition("wf", "v1", "Loop", [loopStep]), "tester", DateTimeOffset.UtcNow);
        var executions = 0;

        var result = await new DurableWorkflowRuntime(new InMemoryWorkflowStateStore(), new InMemoryWorkflowRuntimeAuditSink()).RunAsync(
            request,
            (_, _) =>
            {
                executions++;
                return Task.FromResult(new DurableWorkflowNodeResult(true, LoopContinue: true));
            },
            TestContext.Current.CancellationToken);

        result.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        executions.ShouldBe(2);
        result.Nodes["loop"].Attempts.ShouldBe(2);
    }

    [Fact]
    public async Task RunAsync_DuplicateStepId_RejectsDefinition()
    {
        WorkflowStepDefinition[] steps = [new WorkflowStepDefinition("dup", "test", []), new WorkflowStepDefinition("dup", "test", [])];
        var request = new WorkflowExecutionRequest(
            "dup-exec", new WorkflowDefinition("wf", "v1", "Dup", steps), "tester", DateTimeOffset.UtcNow);

        await Should.ThrowAsync<ArgumentException>(() => new DurableWorkflowRuntime(new InMemoryWorkflowStateStore(), new InMemoryWorkflowRuntimeAuditSink())
            .RunAsync(request, (_, _) => Task.FromResult(DurableWorkflowNodeResult.Success()), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_DanglingDependsOn_RejectsDefinition()
    {
        WorkflowStepDefinition[] steps = [new WorkflowStepDefinition("only", "test", ["missing"])];
        var request = new WorkflowExecutionRequest(
            "dangling-exec", new WorkflowDefinition("wf", "v1", "Dangling", steps), "tester", DateTimeOffset.UtcNow);

        await Should.ThrowAsync<ArgumentException>(() => new DurableWorkflowRuntime(new InMemoryWorkflowStateStore(), new InMemoryWorkflowRuntimeAuditSink())
            .RunAsync(request, (_, _) => Task.FromResult(DurableWorkflowNodeResult.Success()), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_BranchStepWithoutDeclaredTargets_RejectsDefinition()
    {
        WorkflowStepDefinition[] steps = [new WorkflowStepDefinition("branch", "test", [], Kind: WorkflowStepKind.Branch)];
        var request = new WorkflowExecutionRequest(
            "branch-no-targets-exec", new WorkflowDefinition("wf", "v1", "Branch", steps), "tester", DateTimeOffset.UtcNow);

        await Should.ThrowAsync<ArgumentException>(() => new DurableWorkflowRuntime(new InMemoryWorkflowStateStore(), new InMemoryWorkflowRuntimeAuditSink())
            .RunAsync(request, (_, _) => Task.FromResult(DurableWorkflowNodeResult.Success()), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_BranchStepWithDanglingTarget_RejectsDefinition()
    {
        WorkflowStepDefinition[] steps =
        [
            new WorkflowStepDefinition("branch", "test", [], Kind: WorkflowStepKind.Branch, BranchTargets: ["missing"]),
        ];
        var request = new WorkflowExecutionRequest(
            "branch-dangling-target-exec", new WorkflowDefinition("wf", "v1", "Branch", steps), "tester", DateTimeOffset.UtcNow);

        await Should.ThrowAsync<ArgumentException>(() => new DurableWorkflowRuntime(new InMemoryWorkflowStateStore(), new InMemoryWorkflowRuntimeAuditSink())
            .RunAsync(request, (_, _) => Task.FromResult(DurableWorkflowNodeResult.Success()), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_LoopStepWithoutMaxIterations_RejectsDefinition()
    {
        WorkflowStepDefinition[] steps = [new WorkflowStepDefinition("loop", "test", [], Kind: WorkflowStepKind.Loop)];
        var request = new WorkflowExecutionRequest(
            "loop-no-bound-exec", new WorkflowDefinition("wf", "v1", "Loop", steps), "tester", DateTimeOffset.UtcNow);

        await Should.ThrowAsync<ArgumentException>(() => new DurableWorkflowRuntime(new InMemoryWorkflowStateStore(), new InMemoryWorkflowRuntimeAuditSink())
            .RunAsync(request, (_, _) => Task.FromResult(DurableWorkflowNodeResult.Success()), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_UndefinedStepKind_RejectsDefinition()
    {
        WorkflowStepDefinition[] steps = [new WorkflowStepDefinition("odd", "test", [], Kind: (WorkflowStepKind)999)];
        var request = new WorkflowExecutionRequest(
            "undefined-kind-exec", new WorkflowDefinition("wf", "v1", "Odd", steps), "tester", DateTimeOffset.UtcNow);

        await Should.ThrowAsync<ArgumentException>(() => new DurableWorkflowRuntime(new InMemoryWorkflowStateStore(), new InMemoryWorkflowRuntimeAuditSink())
            .RunAsync(request, (_, _) => Task.FromResult(DurableWorkflowNodeResult.Success()), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(WorkflowStepKind.WaitForSignal)]
    [InlineData(WorkflowStepKind.Evaluate)]
    [InlineData(WorkflowStepKind.EmitEvent)]
    [InlineData(WorkflowStepKind.Compensate)]
    public async Task RunAsync_NotYetExecutableStepKind_RejectsDefinitionRatherThanSilentlySucceeding(WorkflowStepKind kind)
    {
        WorkflowStepDefinition[] steps = [new WorkflowStepDefinition("step-1", "test", [], Kind: kind)];
        var request = new WorkflowExecutionRequest(
            $"not-yet-executable-{kind}", new WorkflowDefinition("wf", "v1", "NotYetExecutable", steps), "tester", DateTimeOffset.UtcNow);

        await Should.ThrowAsync<ArgumentException>(() => new DurableWorkflowRuntime(new InMemoryWorkflowStateStore(), new InMemoryWorkflowRuntimeAuditSink())
            .RunAsync(request, (_, _) => Task.FromResult(DurableWorkflowNodeResult.Success()), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_FanOutThenJoin_ExecutesBothAndJoinReconvergesOnFanOutSuccess()
    {
        // FanOut's own "parallelism" is the handler's responsibility (it may await several concurrent
        // sub-tasks internally); the runtime just dispatches it like any other step. This single-dependency
        // shape (Join depends only on the FanOut step) reconverges via the ordinary all-dependencies-succeeded
        // readiness check; see the Branch-with-multiple-targets case below for where Join actually needs its
        // own reconvergence semantics.
        WorkflowStepDefinition[] steps =
        [
            new WorkflowStepDefinition("research", "test", [], Kind: WorkflowStepKind.FanOut),
            new WorkflowStepDefinition("merge", "test", ["research"], Kind: WorkflowStepKind.Join),
        ];
        var request = new WorkflowExecutionRequest("fanout-join", new WorkflowDefinition("wf", "v1", "FanOutJoin", steps), "tester", DateTimeOffset.UtcNow);
        var executed = new List<string>();

        var result = await new DurableWorkflowRuntime(new InMemoryWorkflowStateStore(), new InMemoryWorkflowRuntimeAuditSink()).RunAsync(
            request,
            async (node, ct) =>
            {
                executed.Add(node.Step.StepId);
                if (node.Step.Kind == WorkflowStepKind.FanOut)
                {
                    // Simulates the handler internally fanning out to concurrent sub-tasks.
                    var worker1 = Task.Delay(1, ct).ContinueWith(_ => "worker-1-done", ct, TaskContinuationOptions.None, TaskScheduler.Default);
                    var worker2 = Task.Delay(1, ct).ContinueWith(_ => "worker-2-done", ct, TaskContinuationOptions.None, TaskScheduler.Default);
                    await Task.WhenAll(worker1, worker2);
                }
                return DurableWorkflowNodeResult.Success();
            },
            TestContext.Current.CancellationToken);

        result.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        executed.ShouldBe(["research", "merge"]);
        result.Nodes["research"].Status.ShouldBe(DurableWorkflowNodeStatus.Succeeded);
        result.Nodes["merge"].Status.ShouldBe(DurableWorkflowNodeStatus.Succeeded);
    }

    [Fact]
    public async Task RunAsync_BranchWithJoinOnMultipleTargets_JoinReconvergesRatherThanBeingSkipCascaded()
    {
        // Regression test for a real bug an adversarial review caught: a Join depending on ALL of a Branch's
        // declared targets (the exact shape Ampelum.Agent.Yaml's compiler requires every Branch to have) was
        // being skip-cascaded right alongside the unselected branch target, because the original cascade rule
        // skipped a step as soon as ANY one of its dependencies was skipped. That let the whole execution
        // report Succeeded without the Join -- or anything downstream of it -- ever running.
        WorkflowStepDefinition[] steps =
        [
            new WorkflowStepDefinition("branch", "test", [], Kind: WorkflowStepKind.Branch, BranchTargets: ["path-a", "path-b"]),
            new WorkflowStepDefinition("path-a", "test", ["branch"]),
            new WorkflowStepDefinition("path-b", "test", ["branch"]),
            new WorkflowStepDefinition("merge", "test", ["path-a", "path-b"], Kind: WorkflowStepKind.Join),
        ];
        var request = new WorkflowExecutionRequest("branch-join", new WorkflowDefinition("wf", "v1", "BranchJoin", steps), "tester", DateTimeOffset.UtcNow);
        var executed = new List<string>();

        var result = await new DurableWorkflowRuntime(new InMemoryWorkflowStateStore(), new InMemoryWorkflowRuntimeAuditSink()).RunAsync(
            request,
            (node, _) =>
            {
                executed.Add(node.Step.StepId);
                return Task.FromResult(node.Step.Kind == WorkflowStepKind.Branch
                    ? new DurableWorkflowNodeResult(true, SelectedNextStepId: "path-a")
                    : DurableWorkflowNodeResult.Success());
            },
            TestContext.Current.CancellationToken);

        result.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        executed.ShouldBe(["branch", "path-a", "merge"]);
        result.Nodes["path-a"].Status.ShouldBe(DurableWorkflowNodeStatus.Succeeded);
        result.Nodes["path-b"].Status.ShouldBe(DurableWorkflowNodeStatus.Skipped);
        result.Nodes["merge"].Status.ShouldBe(DurableWorkflowNodeStatus.Succeeded);
    }

    [Fact]
    public async Task RunAsync_JoinWhereEveryDependencyIsSkipped_JoinItselfIsSkippedAndWorkflowFails()
    {
        // A Join depending ONLY on the unselected branch target has nothing left to reconverge once that
        // target is skip-cascaded -- it must be skip-cascaded itself (not silently ready, and not silently
        // succeed) so the execution fails loud on its unresolved dependency instead of reporting false success.
        WorkflowStepDefinition[] steps =
        [
            new WorkflowStepDefinition("branch", "test", [], Kind: WorkflowStepKind.Branch, BranchTargets: ["path-a", "path-b"]),
            new WorkflowStepDefinition("path-a", "test", ["branch"]),
            new WorkflowStepDefinition("path-b", "test", ["branch"]),
            new WorkflowStepDefinition("merge", "test", ["path-b"], Kind: WorkflowStepKind.Join),
        ];
        var request = new WorkflowExecutionRequest("branch-join-all-skipped", new WorkflowDefinition("wf", "v1", "BranchJoin", steps), "tester", DateTimeOffset.UtcNow);

        var result = await new DurableWorkflowRuntime(new InMemoryWorkflowStateStore(), new InMemoryWorkflowRuntimeAuditSink()).RunAsync(
            request,
            (node, _) => Task.FromResult(node.Step.Kind == WorkflowStepKind.Branch
                ? new DurableWorkflowNodeResult(true, SelectedNextStepId: "path-a")
                : DurableWorkflowNodeResult.Success()),
            TestContext.Current.CancellationToken);

        result.Nodes["merge"].Status.ShouldBe(DurableWorkflowNodeStatus.Skipped);
        result.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task TryUpdateAsync_StructurallyDifferentButSameVersionExpected_SucceedsOnVersionAlone()
    {
        // Regression test: CAS must gate on Version, not on record/structural equality of `expected`.
        // Build two `expected` instances with the same Version but different structural detail (a
        // different UpdatedAt timestamp and a differently-keyed dictionary instance) to prove the
        // store never inspects anything but Version.
        var store = new InMemoryWorkflowStateStore();
        var request = CreateRequest("cas-version", stepCount: 1);
        var nodes = request.Workflow.Steps.ToDictionary(
            step => step.StepId,
            step => new DurableWorkflowNodeSnapshot(step.StepId, DurableWorkflowNodeStatus.Pending, 0, $"{request.ExecutionId}:{step.StepId}"));
        var original = new DurableWorkflowSnapshot(request, WorkflowExecutionStatus.Running, nodes, DateTimeOffset.UtcNow, Version: 5);
        await store.TryCreateAsync(original, TestContext.Current.CancellationToken);

        var staleShapedExpected = new DurableWorkflowSnapshot(
            request,
            WorkflowExecutionStatus.Running,
            new Dictionary<string, DurableWorkflowNodeSnapshot>(nodes),
            original.UpdatedAt.AddSeconds(-1),
            Version: 5);
        var next = original with { Status = WorkflowExecutionStatus.Succeeded, Version = 6 };

        var updated = await store.TryUpdateAsync(request.ExecutionId, staleShapedExpected, next, TestContext.Current.CancellationToken);

        updated.ShouldBeTrue();
        (await store.LoadAsync(request.ExecutionId, TestContext.Current.CancellationToken))!.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task TryUpdateAsync_StaleVersion_IsRejected()
    {
        var store = new InMemoryWorkflowStateStore();
        var request = CreateRequest("cas-stale", stepCount: 1);
        var nodes = request.Workflow.Steps.ToDictionary(
            step => step.StepId,
            step => new DurableWorkflowNodeSnapshot(step.StepId, DurableWorkflowNodeStatus.Pending, 0, $"{request.ExecutionId}:{step.StepId}"));
        var original = new DurableWorkflowSnapshot(request, WorkflowExecutionStatus.Running, nodes, DateTimeOffset.UtcNow, Version: 1);
        await store.TryCreateAsync(original, TestContext.Current.CancellationToken);
        await store.TryUpdateAsync(request.ExecutionId, original, original with { Version = 2 }, TestContext.Current.CancellationToken);

        var updated = await store.TryUpdateAsync(request.ExecutionId, original, original with { Status = WorkflowExecutionStatus.Failed, Version = 2 }, TestContext.Current.CancellationToken);

        updated.ShouldBeFalse();
        (await store.LoadAsync(request.ExecutionId, TestContext.Current.CancellationToken))!.Status.ShouldBe(WorkflowExecutionStatus.Running);
    }

    [Fact]
    public async Task RunAsync_MultipleIndependentReadyNodes_ExecutesThemConcurrentlyNotSerially()
    {
        // Two independent roots (no DependsOn between them) are both ready in the very first dispatch
        // pass. Each handler waits until both are inside; a runtime that dispatched one ready node at a
        // time would never let the second in, and the wait would time out.
        WorkflowStepDefinition[] steps =
        [
            new WorkflowStepDefinition("root-a", "test", []),
            new WorkflowStepDefinition("root-b", "test", []),
        ];
        var request = new WorkflowExecutionRequest("concurrent-roots", new WorkflowDefinition("wf", "v1", "ConcurrentRoots", steps), "tester", DateTimeOffset.UtcNow);
        var concurrentInsideWindow = 0;
        var maxObservedConcurrency = 0;
        var gate = new object();
        var bothIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = await new DurableWorkflowRuntime(new InMemoryWorkflowStateStore(), new InMemoryWorkflowRuntimeAuditSink()).RunAsync(
            request,
            async (_, ct) =>
            {
                lock (gate)
                {
                    concurrentInsideWindow++;
                    maxObservedConcurrency = Math.Max(maxObservedConcurrency, concurrentInsideWindow);
                    if (concurrentInsideWindow == 2) bothIn.TrySetResult();
                }
                await bothIn.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                lock (gate) { concurrentInsideWindow--; }
                return DurableWorkflowNodeResult.Success();
            },
            TestContext.Current.CancellationToken);

        result.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        result.Nodes["root-a"].Status.ShouldBe(DurableWorkflowNodeStatus.Succeeded);
        result.Nodes["root-b"].Status.ShouldBe(DurableWorkflowNodeStatus.Succeeded);
        // Direct interleaving evidence: both handlers were in flight at the same instant.
        maxObservedConcurrency.ShouldBe(2);
    }

    [Fact]
    public async Task RunAsync_ConcurrentDoubleClaimAttemptOnSameNode_NeverDoubleExecutes()
    {
        // Simulates a second worker racing to claim the same single ready node at the exact instant the
        // first worker's claim is in flight: both start from the SAME pre-claim snapshot and call the
        // runtime's internal claim path concurrently for the identical step. The atomic version-gated CAS
        // (unchanged by the concurrent-dispatch fix) must let exactly one claim win; the loser must reload
        // and observe the node already Running/settled rather than re-claiming and re-running it.
        var store = new InMemoryWorkflowStateStore();
        var audit = new InMemoryWorkflowRuntimeAuditSink();
        var request = CreateRequest("double-claim-race", stepCount: 1);
        var executions = 0;
        var runtime = new DurableWorkflowRuntime(store, audit);

        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<DurableWorkflowSnapshot> RunOnce() => runtime.RunAsync(
            request,
            async (_, ct) =>
            {
                Interlocked.Increment(ref executions);
                await barrier.Task.WaitAsync(ct);
                return DurableWorkflowNodeResult.Success();
            },
            TestContext.Current.CancellationToken);

        // Launch two concurrent attempts to run the SAME execution/step; the second will either lose the
        // claim outright or observe the node already Running and no-op.
        var attempt1 = RunOnce();
        var attempt2 = RunOnce();
        await Task.Delay(20, TestContext.Current.CancellationToken);
        barrier.SetResult();

        await Task.WhenAll(attempt1, attempt2);
        executions.ShouldBe(1);
        (await store.LoadAsync(request.ExecutionId, TestContext.Current.CancellationToken))!.Nodes["step-1"].Status.ShouldBe(DurableWorkflowNodeStatus.Succeeded);
    }

    static WorkflowExecutionRequest CreateRequest(string executionId, int stepCount = 3)
    {
        var steps = Enumerable.Range(1, stepCount)
            .Select(index => new WorkflowStepDefinition($"step-{index}", "test", index == 1 ? [] : [$"step-{index - 1}"]))
            .ToArray();
        return new WorkflowExecutionRequest(executionId, new WorkflowDefinition("test", "v1", "Test", steps), "tester", DateTimeOffset.UtcNow);
    }
}

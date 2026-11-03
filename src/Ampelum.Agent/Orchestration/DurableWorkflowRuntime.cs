using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using SignalGrid.Ampelum.Agent.Contracts;

namespace SignalGrid.Ampelum.Agent.Orchestration;

/// <summary>Durable state of a single declarative workflow node.</summary>
public enum DurableWorkflowNodeStatus
{
    Pending, Running, Succeeded, Failed, Cancelled, Skipped,
    /// <summary>The node waits for a decision delivered through <see cref="DurableWorkflowRuntime.DecideAsync"/>, or for a signal delivered through <see cref="DurableWorkflowRuntime.SignalAsync"/>.</summary>
    Waiting,
}

/// <summary>Outcome returned by a durable workflow node handler.</summary>
/// <param name="Succeeded">Whether the node completed successfully.</param>
/// <param name="Retryable">Whether a failed outcome should be retried, up to the runtime's configured maximum attempts.</param>
/// <param name="Detail">Optional human-readable detail recorded alongside the outcome. For a waiting outcome, the reason the node waits.</param>
/// <param name="SelectedNextStepId">For a <see cref="WorkflowStepKind.Branch"/> step, the declared branch target this outcome selects.</param>
/// <param name="LoopContinue">For a <see cref="WorkflowStepKind.Loop"/> step, whether the loop body should re-execute (subject to the step's <c>MaxIterations</c> bound).</param>
/// <param name="Waiting">Whether the node waits for a decision instead of finishing. A waiting outcome is never a success or a failure.</param>
public sealed record DurableWorkflowNodeResult(
    bool Succeeded,
    bool Retryable = false,
    string? Detail = null,
    string? SelectedNextStepId = null,
    bool LoopContinue = false,
    bool Waiting = false)
{
    /// <summary>Creates a successful node outcome.</summary>
    public static DurableWorkflowNodeResult Success(string? detail = null) => new(true, false, detail);

    /// <summary>Creates a failed node outcome.</summary>
    public static DurableWorkflowNodeResult Failure(string? detail = null, bool retryable = false) => new(false, retryable, detail);

    /// <summary>Creates an outcome that parks the node until a decision is delivered for it.</summary>
    /// <param name="reason">Why a decision is needed.</param>
    public static DurableWorkflowNodeResult Wait(string? reason = null) => new(false, false, reason, Waiting: true);
}

/// <summary>A decision delivered for a waiting node.</summary>
/// <param name="Approved">Whether the decider approved.</param>
/// <param name="DecidedBy">Identity of the decider.</param>
/// <param name="Reason">Optional reason or payload the decider supplied.</param>
public sealed record DurableWorkflowDecision(bool Approved, string DecidedBy, string? Reason = null);

/// <summary>A signal delivered to a <see cref="WorkflowStepKind.WaitForSignal"/> node.</summary>
/// <param name="Name">The signal name.</param>
/// <param name="Payload">Optional payload the sender supplied.</param>
/// <param name="SentBy">Identity of the sender.</param>
public sealed record DurableWorkflowSignal(string Name, string? Payload, string SentBy);

/// <summary>Persisted node state, including its stable idempotency key.</summary>
/// <param name="StepId">The declared workflow step identifier this snapshot tracks.</param>
/// <param name="Status">The node's current durable execution status.</param>
/// <param name="Attempts">The number of times this node has been claimed for execution, including retries.</param>
/// <param name="IdempotencyKey">Stable key handlers can use to deduplicate a retried or recovered side effect.</param>
/// <param name="Detail">Optional human-readable detail recorded alongside the node's last outcome.</param>
/// <param name="LoopIterations">
/// For a <see cref="WorkflowStepKind.Loop"/> step, the number of loop-body executions that have completed
/// successfully so far. Tracked separately from <paramref name="Attempts"/> so a retry of a failed attempt does
/// not silently consume the caller's declared <c>MaxIterations</c> budget.
/// </param>
/// <param name="Decision">The latest decision delivered for this node, if any.</param>
/// <param name="Signal">For a <see cref="WorkflowStepKind.WaitForSignal"/> step, the signal delivered to it, if any.</param>
/// <param name="Observations">For an <see cref="WorkflowStepKind.Evaluate"/> step, the metric observations of its last attempt.</param>
/// <param name="CompletionOrder">Position in which this node succeeded among all nodes of the execution, starting at 1; 0 while it has not succeeded. Compensation runs in reverse of this order. Nodes that finish in one batch are numbered in the order their outcomes are recorded, which is the order of the workflow's step list.</param>
public sealed record DurableWorkflowNodeSnapshot(
    string StepId,
    DurableWorkflowNodeStatus Status,
    int Attempts,
    string IdempotencyKey,
    string? Detail = null,
    int LoopIterations = 0,
    DurableWorkflowDecision? Decision = null,
    DurableWorkflowSignal? Signal = null,
    IReadOnlyList<MetricObservation>? Observations = null,
    int CompletionOrder = 0);

/// <summary>Persisted durable execution checkpoint.</summary>
/// <param name="Request">The execution request this checkpoint tracks.</param>
/// <param name="Status">The execution's current lifecycle status.</param>
/// <param name="Nodes">Durable per-step node state, keyed by step identifier.</param>
/// <param name="UpdatedAt">When this checkpoint was last written.</param>
/// <param name="FailureReason">Human-readable failure detail, set only when <paramref name="Status"/> is <see cref="WorkflowExecutionStatus.Failed"/>.</param>
/// <param name="Version">
/// Monotonically increasing checkpoint version, bumped on every transition. Persistence providers gate
/// optimistic-concurrency compare-and-swap on this value rather than snapshot content equality, so the
/// three provider implementations (in-memory, SQLite, PostgreSQL) share one CAS contract independent of
/// record structural equality or JSON serialization determinism.
/// </param>
public sealed record DurableWorkflowSnapshot(
    WorkflowExecutionRequest Request,
    WorkflowExecutionStatus Status,
    IReadOnlyDictionary<string, DurableWorkflowNodeSnapshot> Nodes,
    DateTimeOffset UpdatedAt,
    string? FailureReason = null,
    long Version = 0);

/// <summary>Atomic persistence boundary for durable workflow checkpoints.</summary>
public interface IWorkflowStateStore
{
    /// <summary>Loads the latest checkpoint for an execution.</summary>
    Task<DurableWorkflowSnapshot?> LoadAsync(string executionId, CancellationToken ct = default);

    /// <summary>Creates an execution if it does not already exist.</summary>
    Task<bool> TryCreateAsync(DurableWorkflowSnapshot snapshot, CancellationToken ct = default);

    /// <summary>
    /// Atomically replaces a checkpoint only when its current <see cref="DurableWorkflowSnapshot.Version"/>
    /// still matches <paramref name="expected"/>'s. Implementations gate solely on <c>Version</c>, not on
    /// structural or reference equality of <paramref name="expected"/> — callers must pass <paramref name="next"/>
    /// with <c>Version == expected.Version + 1</c>; passing an unincremented or already-used version silently
    /// disables the compare-and-swap for that execution.
    /// </summary>
    Task<bool> TryUpdateAsync(string executionId, DurableWorkflowSnapshot expected, DurableWorkflowSnapshot next, CancellationToken ct = default);
}

/// <summary>Optional transactional persistence boundary for a checkpoint and its audit event.</summary>
/// <remarks>Use the same instance as both the state store and audit sink when the provider supports this contract.</remarks>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IWorkflowCheckpointTransactionStore : IWorkflowStateStore, IWorkflowRuntimeAuditSink
{
    /// <summary>Atomically creates a checkpoint and appends its first audit event.</summary>
    Task<bool> TryCreateAndAppendAsync(DurableWorkflowSnapshot snapshot, WorkflowRuntimeAuditEvent auditEvent, CancellationToken ct = default);

    /// <summary>Atomically compares, replaces a checkpoint, and appends its audit event.</summary>
    Task<bool> TryUpdateAndAppendAsync(string executionId, DurableWorkflowSnapshot expected, DurableWorkflowSnapshot next, WorkflowRuntimeAuditEvent auditEvent, CancellationToken ct = default);
}

/// <summary>Append-only audit event emitted for every runtime state transition and node outcome.</summary>
public sealed record WorkflowRuntimeAuditEvent(
    long Sequence,
    string ExecutionId,
    string EventType,
    string? StepId,
    WorkflowExecutionStatus Status,
    DateTimeOffset OccurredAt,
    string? Detail = null);

/// <summary>Receives runtime audit events. Implementations must preserve event order for one execution.</summary>
public interface IWorkflowRuntimeAuditSink
{
    /// <summary>Appends one runtime audit event.</summary>
    Task AppendAsync(WorkflowRuntimeAuditEvent auditEvent, CancellationToken ct = default);
}

/// <summary>Thread-safe in-memory checkpoint store for deterministic tests and embedded use.</summary>
public sealed class InMemoryWorkflowStateStore : IWorkflowStateStore
{
    readonly object _gate = new();
    readonly Dictionary<string, DurableWorkflowSnapshot> _snapshots = [];

    /// <inheritdoc />
    public Task<DurableWorkflowSnapshot?> LoadAsync(string executionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        lock (_gate)
            return Task.FromResult(_snapshots.GetValueOrDefault(executionId));
    }

    /// <inheritdoc />
    public Task<bool> TryCreateAsync(DurableWorkflowSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            if (_snapshots.ContainsKey(snapshot.Request.ExecutionId)) return Task.FromResult(false);
            _snapshots.Add(snapshot.Request.ExecutionId, snapshot);
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<bool> TryUpdateAsync(string executionId, DurableWorkflowSnapshot expected, DurableWorkflowSnapshot next, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(next);
        lock (_gate)
        {
            if (!_snapshots.TryGetValue(executionId, out var current) || current.Version != expected.Version) return Task.FromResult(false);
            _snapshots[executionId] = next;
            return Task.FromResult(true);
        }
    }
}

/// <summary>In-memory ordered audit sink for tests and local diagnostics.</summary>
public sealed class InMemoryWorkflowRuntimeAuditSink : IWorkflowRuntimeAuditSink
{
    readonly ConcurrentQueue<WorkflowRuntimeAuditEvent> _events = new();

    /// <summary>Recorded events in append order.</summary>
    public IReadOnlyList<WorkflowRuntimeAuditEvent> Events => _events.ToArray();

    /// <inheritdoc />
    public Task AppendAsync(WorkflowRuntimeAuditEvent auditEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        _events.Enqueue(auditEvent);
        return Task.CompletedTask;
    }
}

/// <summary>Execution context supplied to every durable node handler.</summary>
public sealed record DurableWorkflowNodeContext(
    WorkflowExecutionRequest Request,
    WorkflowStepDefinition Step,
    string IdempotencyKey,
    int Attempt,
    DurableWorkflowDecision? Decision = null)
{
    /// <summary>
    /// Read-only snapshots of the step's direct dependencies, keyed by step id, so a step can read what they stored
    /// (a delivered signal, a detail). For a <see cref="WorkflowStepKind.Compensate"/> step it holds the step it undoes.
    /// </summary>
    public IReadOnlyDictionary<string, DurableWorkflowNodeSnapshot> Dependencies { get; init; } = new Dictionary<string, DurableWorkflowNodeSnapshot>();
}

/// <summary>What an evaluator receives for one <see cref="WorkflowStepKind.Evaluate"/> attempt.</summary>
/// <param name="Request">The execution request.</param>
/// <param name="Step">The Evaluate step.</param>
/// <param name="MetricIds">The metrics of the workflow's evaluation plan that the evaluator must observe.</param>
/// <param name="IdempotencyKey">Stable key of the node.</param>
/// <param name="Attempt">The attempt number.</param>
/// <param name="Dependencies">Read-only snapshots of the step's direct dependencies, keyed by step id.</param>
public sealed record DurableWorkflowEvaluationContext(
    WorkflowExecutionRequest Request,
    WorkflowStepDefinition Step,
    IReadOnlyList<string> MetricIds,
    string IdempotencyKey,
    int Attempt,
    IReadOnlyDictionary<string, DurableWorkflowNodeSnapshot> Dependencies);

/// <summary>Observes the metrics of a workflow's evaluation plan for an <see cref="WorkflowStepKind.Evaluate"/> step.</summary>
public interface IDurableWorkflowEvaluator
{
    /// <summary>Returns one observation for each metric in <see cref="DurableWorkflowEvaluationContext.MetricIds"/>.</summary>
    Task<IReadOnlyList<MetricObservation>> EvaluateAsync(DurableWorkflowEvaluationContext context, CancellationToken ct);
}

/// <summary>
/// Generic durable runtime for declarative workflow definitions. Completed node keys are never re-executed;
/// handlers receive a stable key so an interrupted in-flight call can be safely deduplicated by its side-effect boundary.
/// </summary>
public sealed class DurableWorkflowRuntime
{
    readonly IWorkflowStateStore _store;
    readonly IWorkflowRuntimeAuditSink _audit;
    readonly int _maxAttempts;
    readonly int _maxConcurrency;
    readonly IDurableWorkflowEvaluator? _evaluator;
    long _sequence;

    /// <summary>Creates a durable workflow runtime.</summary>
    /// <param name="store">Atomic checkpoint persistence.</param>
    /// <param name="audit">Append-only runtime audit sink.</param>
    /// <param name="maxAttempts">Maximum claim attempts (including retries) for a single node.</param>
    /// <param name="maxConcurrency">
    /// Upper bound on how many independently-ready nodes are claimed and dispatched concurrently within
    /// one dispatch pass. Bounded rather than unbounded so a wide DAG (a fan-out into hundreds of ready
    /// leaves) cannot flood the caller's handler (and, transitively, downstream LLM/tool/HTTP endpoints)
    /// with unlimited simultaneous in-flight calls. 8 mirrors the bounded-parallelism default this repo
    /// already uses elsewhere for handler fan-out (e.g. tool dispatch); callers with wider or narrower
    /// concurrency budgets can override it explicitly.
    /// </param>
    /// <param name="evaluator">
    /// Observes the metrics of the workflow's evaluation plan for <see cref="WorkflowStepKind.Evaluate"/> steps.
    /// A workflow with such a step is rejected when this is null.
    /// </param>
    public DurableWorkflowRuntime(IWorkflowStateStore store, IWorkflowRuntimeAuditSink audit, int maxAttempts = 3, int maxConcurrency = 8, IDurableWorkflowEvaluator? evaluator = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        if (maxConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        _maxAttempts = maxAttempts;
        _maxConcurrency = maxConcurrency;
        _evaluator = evaluator;
    }

    /// <summary>Starts or resumes an execution and runs ready nodes in declaration order.</summary>
    public async Task<DurableWorkflowSnapshot> RunAsync(
        WorkflowExecutionRequest request,
        Func<DurableWorkflowNodeContext, CancellationToken, Task<DurableWorkflowNodeResult>> handler,
        CancellationToken ct = default) => await RunCoreAsync(request, handler, recoverInFlightNodes: false, ct).ConfigureAwait(false);

    /// <summary>Resumes an execution after process restart, recovering in-flight nodes with their original idempotency keys.</summary>
    public async Task<DurableWorkflowSnapshot> ResumeAsync(
        WorkflowExecutionRequest request,
        Func<DurableWorkflowNodeContext, CancellationToken, Task<DurableWorkflowNodeResult>> handler,
        CancellationToken ct = default) => await RunCoreAsync(request, handler, recoverInFlightNodes: true, ct).ConfigureAwait(false);

    async Task<DurableWorkflowSnapshot> RunCoreAsync(
        WorkflowExecutionRequest request,
        Func<DurableWorkflowNodeContext, CancellationToken, Task<DurableWorkflowNodeResult>> handler,
        bool recoverInFlightNodes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(handler);
        ValidateDefinition(request.Workflow);
        // Compensate steps never run in the forward flow, so they are left out of readiness and completion checks.
        var compensateIds = request.Workflow.Steps.Where(step => step.Kind == WorkflowStepKind.Compensate).Select(step => step.StepId).ToHashSet(StringComparer.Ordinal);
        Func<DurableWorkflowSnapshot, string, Task<DurableWorkflowSnapshot>> failExecution = (current, reason) => FailExecutionAsync(request, handler, current, reason, ct);

        var snapshot = await GetOrCreateAsync(request, ct).ConfigureAwait(false);
        if (!IsTerminal(snapshot.Status))
        {
            var missingStepId = request.Workflow.Steps.Select(step => step.StepId).FirstOrDefault(stepId => !snapshot.Nodes.ContainsKey(stepId));
            if (missingStepId is not null)
                return await FailAsync(snapshot, $"Resumed workflow definition declares step '{missingStepId}' that is absent from the persisted checkpoint.", ct).ConfigureAwait(false);
        }
        if (recoverInFlightNodes)
            snapshot = await RecoverInFlightNodesAsync(snapshot, ct).ConfigureAwait(false);
        while (!IsTerminal(snapshot.Status))
        {
            if (ct.IsCancellationRequested)
                return await CancelAsync(snapshot, "Cancellation requested.", CancellationToken.None).ConfigureAwait(false);

            // A rejected decision that left compensations to run keeps the execution open with its failure reason set;
            // nothing else starts, and the compensations run before the execution fails. Only DecideAsync sets a
            // failure reason on an execution that is not terminal.
            if (snapshot.FailureReason is { } rejection)
                return await failExecution(snapshot, rejection).ConfigureAwait(false);

            // Dispatch the WHOLE ready frontier concurrently, not just the first ready step: a DAG with
            // several simultaneously-ready independent branches (e.g. downstream of a FanOut, or simply
            // several roots with no DependsOn) previously executed strictly one node at a time even though
            // nothing coupled them. Bounded by _maxConcurrency below.
            var readySteps = request.Workflow.Steps.Where(candidate => IsReady(candidate, snapshot.Nodes)).ToList();
            if (readySteps.Count == 0)
            {
                // A failed node means the execution has failed, whatever else is left; it is never reported as a success.
                var failedCause = FailureCause(snapshot, compensateIds);
                if (failedCause is not null)
                    return await failExecution(snapshot, failedCause).ConfigureAwait(false);
                if (snapshot.Nodes.Values.Any(node => node.Status == DurableWorkflowNodeStatus.Running))
                    return snapshot;
                if (snapshot.Nodes.Values.Any(node => node.Status == DurableWorkflowNodeStatus.Waiting))
                {
                    if (snapshot.Status != WorkflowExecutionStatus.AwaitingConfirmation)
                        snapshot = await AwaitConfirmationAsync(snapshot, ct).ConfigureAwait(false);
                    // A lost swap hands back a changed checkpoint: scan it again instead of reporting a stale state.
                    if (snapshot.Status == WorkflowExecutionStatus.AwaitingConfirmation) return snapshot;
                    continue;
                }
                var incomplete = snapshot.Nodes.Values.Any(node => node.Status is DurableWorkflowNodeStatus.Pending or DurableWorkflowNodeStatus.Running && !compensateIds.Contains(node.StepId));
                if (incomplete)
                    return await failExecution(snapshot, "Workflow contains unresolved dependencies.").ConfigureAwait(false);
                snapshot = await SucceedAsync(snapshot, compensateIds, ct).ConfigureAwait(false);
                continue;
            }

            // Approval, WaitForSignal and EmitEvent steps never reach the handler: the runtime parks or settles
            // them itself, then re-scans readiness.
            var nativeSteps = readySteps.Where(step => step.Kind is WorkflowStepKind.Approval or WorkflowStepKind.WaitForSignal or WorkflowStepKind.EmitEvent).ToList();
            if (nativeSteps.Count > 0)
            {
                foreach (var nativeStep in nativeSteps)
                    snapshot = await SettleNativeStepAsync(snapshot, nativeStep, ct).ConfigureAwait(false);
                continue;
            }

            snapshot = await ClaimReadyBatchAsync(snapshot, readySteps, ct).ConfigureAwait(false);
            var claimedSteps = readySteps.Where(step => snapshot.Nodes[step.StepId].Status == DurableWorkflowNodeStatus.Running).ToList();
            // Every ready step's claim lost its CAS race this pass (another instance, or a concurrent
            // iteration, claimed all of them first) -- nothing of ours to dispatch; re-scan readiness.
            if (claimedSteps.Count == 0) continue;

            var outcomes = await DispatchClaimedBatchAsync(request, snapshot, claimedSteps, handler, ct).ConfigureAwait(false);

            // Matches the pre-batch, single-node contract exactly: if cancellation was requested by (or
            // observed during) this dispatch pass -- whether a handler threw OperationCanceledException or
            // simply cancelled the shared token synchronously before returning -- none of this pass's
            // outcomes are persisted; the runtime transitions straight to Cancelled using the pre-dispatch
            // (post-claim) snapshot, same as the original serial loop did for its one in-flight node.
            if (ct.IsCancellationRequested)
                return await CancelAsync(snapshot, "Cancellation requested.", CancellationToken.None).ConfigureAwait(false);

            // Every outcome of the batch is recorded before the execution fails, so a sibling that succeeded is a
            // completed step and its compensation runs. Within a batch, CompletionOrder follows this recording order.
            foreach (var (step, outcome, evaluation) in outcomes)
                snapshot = await RecordOutcomeAsync(snapshot, step, outcome, evaluation, ct).ConfigureAwait(false);
            var batchCause = FailureCause(snapshot, compensateIds);
            if (batchCause is not null)
                return await failExecution(snapshot, batchCause).ConfigureAwait(false);
        }

        return snapshot;
    }

    /// <summary>
    /// Attempts to claim every step in <paramref name="readySteps"/> concurrently. Each individual claim
    /// still goes through <see cref="ClaimAsync"/>'s per-node optimistic-concurrency compare-and-swap
    /// against the shared checkpoint's <see cref="DurableWorkflowSnapshot.Version"/> unchanged -- issuing
    /// several claims concurrently only means several CAS attempts race the store instead of one running
    /// at a time; the store still accepts exactly one writer per version, so two callers (two concurrent
    /// batches within this runtime, or two runtime instances/processes) can never both flip the same node
    /// to <see cref="DurableWorkflowNodeStatus.Running"/> for the same version history. Losing the race
    /// costs a reload-and-retry, never a double-claim.
    /// </summary>
    async Task<DurableWorkflowSnapshot> ClaimReadyBatchAsync(DurableWorkflowSnapshot snapshot, IReadOnlyList<WorkflowStepDefinition> readySteps, CancellationToken ct)
    {
        if (readySteps.Count == 1)
            return await ClaimAsync(snapshot, readySteps[0], ct).ConfigureAwait(false);

        await Task.WhenAll(readySteps.Select(step => ClaimAsync(snapshot, step, ct))).ConfigureAwait(false);
        // Each concurrent ClaimAsync call above raced the store independently and may hold a stale view of
        // sibling claims it didn't itself perform -- reload once for an authoritative post-batch snapshot
        // rather than trusting any single call's returned value.
        return await _store.LoadAsync(snapshot.Request.ExecutionId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Workflow checkpoint disappeared during batch claim.");
    }

    /// <summary>
    /// Runs the claimed batch's handlers concurrently, bounded by <see cref="_maxConcurrency"/>. A node
    /// whose handler observed cancellation returns a <c>null</c> outcome; the caller checks
    /// <paramref name="ct"/> once after the whole batch settles (see the loop in <see cref="RunCoreAsync"/>)
    /// rather than per-node, so a handler that cancels the shared token synchronously (without itself
    /// throwing) is caught exactly like one that throws <see cref="OperationCanceledException"/>.
    /// </summary>
    async Task<(WorkflowStepDefinition Step, DurableWorkflowNodeResult Outcome, Evaluation? Evaluation)[]> DispatchClaimedBatchAsync(
        WorkflowExecutionRequest request,
        DurableWorkflowSnapshot snapshot,
        IReadOnlyList<WorkflowStepDefinition> claimedSteps,
        Func<DurableWorkflowNodeContext, CancellationToken, Task<DurableWorkflowNodeResult>> handler,
        CancellationToken ct)
    {
        using var throttle = new SemaphoreSlim(Math.Min(_maxConcurrency, claimedSteps.Count));
        var results = await Task.WhenAll(claimedSteps.Select(step => DispatchOneAsync(step))).ConfigureAwait(false);
        return [.. results.Where(result => result.Outcome is not null).Select(result => (result.Step, result.Outcome!, result.Evaluation))];

        async Task<(WorkflowStepDefinition Step, DurableWorkflowNodeResult? Outcome, Evaluation? Evaluation)> DispatchOneAsync(WorkflowStepDefinition step)
        {
            await throttle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var node = snapshot.Nodes[step.StepId];
                var dependencies = DependenciesOf(step, snapshot.Nodes);
                if (step.Kind == WorkflowStepKind.Evaluate)
                {
                    var (evaluated, evaluation) = await EvaluateAsync(new DurableWorkflowEvaluationContext(request, step, request.Workflow.EvaluationPlan!.MetricIds, node.IdempotencyKey, node.Attempts, dependencies), ct).ConfigureAwait(false);
                    return (step, evaluated, evaluation);
                }
                var context = new DurableWorkflowNodeContext(request, step, node.IdempotencyKey, node.Attempts, node.Decision) { Dependencies = dependencies };
                return (step, await InvokeHandlerAsync(handler, context, ct).ConfigureAwait(false), null);
            }
            finally
            {
                throttle.Release();
            }
        }
    }

    // A handler that observed cancellation yields null; any other exception becomes a non-retryable failure.
    static async Task<DurableWorkflowNodeResult?> InvokeHandlerAsync(
        Func<DurableWorkflowNodeContext, CancellationToken, Task<DurableWorkflowNodeResult>> handler,
        DurableWorkflowNodeContext context,
        CancellationToken ct)
    {
        try
        {
            return await handler(context, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            return DurableWorkflowNodeResult.Failure(ex.Message, retryable: false);
        }
    }

    // One evaluator call per attempt, treated like a handler call: cancellation yields null, an exception a failure.
    async Task<(DurableWorkflowNodeResult? Outcome, Evaluation? Evaluation)> EvaluateAsync(DurableWorkflowEvaluationContext context, CancellationToken ct)
    {
        try
        {
            var observations = await _evaluator!.EvaluateAsync(context, ct).ConfigureAwait(false);
            return Judge(context.Request.Workflow, observations);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (null, null);
        }
        catch (Exception ex)
        {
            return (DurableWorkflowNodeResult.Failure(ex.Message, retryable: false), null);
        }
    }

    // What an evaluator returned and one audit line about it: every observed metric with its value and threshold result.
    sealed record Evaluation(IReadOnlyList<MetricObservation> Observations, string Summary);

    // A plan metric the evaluator left out fails the node. So does every observed metric, in the plan or not, that
    // the objective gives a threshold and that misses it.
    static (DurableWorkflowNodeResult Outcome, Evaluation Evaluation) Judge(WorkflowDefinition workflow, IReadOnlyList<MetricObservation> observations)
    {
        var misses = new List<string>();
        foreach (var metricId in workflow.EvaluationPlan!.MetricIds)
            if (!observations.Any(candidate => candidate.MetricId == metricId))
                misses.Add($"Metric '{metricId}' was not returned by the evaluator.");

        var lines = new List<string>();
        foreach (var metricId in observations.Select(candidate => candidate.MetricId).Distinct())
        {
            var value = observations.Last(candidate => candidate.MetricId == metricId).Value;
            var text = value.ToString(CultureInfo.InvariantCulture);
            var goal = workflow.Objective?.Metrics.FirstOrDefault(metric => metric.MetricId == metricId && metric.Threshold is not null);
            if (goal is null)
            {
                lines.Add($"{metricId}={text}");
                continue;
            }
            var threshold = goal.Threshold!.Value;
            bool? met = goal.Direction switch
            {
                "maximize" => value >= threshold,
                "minimize" => value <= threshold,
                _ => null,
            };
            var thresholdText = threshold.ToString(CultureInfo.InvariantCulture);
            if (met is null)
            {
                lines.Add($"{metricId}={text} (unknown direction '{goal.Direction}')");
                misses.Add($"Metric '{metricId}' has unknown direction '{goal.Direction}'.");
            }
            else if (met.Value)
            {
                lines.Add($"{metricId}={text} (met {goal.Direction} threshold {thresholdText})");
            }
            else
            {
                lines.Add($"{metricId}={text} (missed {goal.Direction} threshold {thresholdText})");
                misses.Add($"Metric '{metricId}' value {text} misses threshold {thresholdText} ({goal.Direction}).");
            }
        }

        var outcome = misses.Count == 0
            ? DurableWorkflowNodeResult.Success($"Evaluated {lines.Count} metric(s).")
            : DurableWorkflowNodeResult.Failure(string.Join(" ", misses));
        return (outcome, new Evaluation(observations, string.Join("; ", lines)));
    }

    static Dictionary<string, DurableWorkflowNodeSnapshot> DependenciesOf(WorkflowStepDefinition step, IReadOnlyDictionary<string, DurableWorkflowNodeSnapshot> nodes) =>
        step.DependsOn.ToDictionary(dependency => dependency, dependency => nodes[dependency], StringComparer.Ordinal);

    async Task<DurableWorkflowSnapshot> GetOrCreateAsync(WorkflowExecutionRequest request, CancellationToken ct)
    {
        var existing = await _store.LoadAsync(request.ExecutionId, ct).ConfigureAwait(false);
        if (existing is not null) return existing;
        var nodes = request.Workflow.Steps.ToDictionary(
            step => step.StepId,
            step => new DurableWorkflowNodeSnapshot(step.StepId, DurableWorkflowNodeStatus.Pending, 0, $"{request.ExecutionId}:{step.StepId}"));
        var created = new DurableWorkflowSnapshot(request, WorkflowExecutionStatus.Running, nodes, DateTimeOffset.UtcNow);
        if (await TryCreateAndAuditAsync(created, "execution.started", null, null, ct).ConfigureAwait(false))
        {
            return created;
        }
        return await _store.LoadAsync(request.ExecutionId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Workflow checkpoint was not available after a concurrent create.");
    }

    async Task<DurableWorkflowSnapshot> ClaimAsync(DurableWorkflowSnapshot snapshot, WorkflowStepDefinition step, CancellationToken ct)
    {
        while (true)
        {
            var node = snapshot.Nodes[step.StepId];
            // Only a Pending node may be claimed. Bailing out on any other status -- not just Succeeded --
            // is the guarantee that makes concurrent claiming safe: if a reload after a lost CAS race shows
            // this node already Running (another concurrent claim in this same batch won it, another
            // runtime instance/process claimed it, or it settled to Failed/Cancelled/Skipped meanwhile),
            // this call must never re-claim and re-run it. Before this bailed only on Succeeded, so a
            // concurrent claimant that lost the race would reload, see Running, and (using the reload's
            // stale-relative-to-itself Attempts) attempt to re-claim and re-run the same node -- a latent
            // double-execution hazard that concurrent dispatch would otherwise turn from theoretical into
            // routine.
            if (node.Status != DurableWorkflowNodeStatus.Pending) return snapshot;
            var nodes = snapshot.Nodes.ToDictionary(pair => pair.Key, pair => pair.Value);
            nodes[step.StepId] = node with { Status = DurableWorkflowNodeStatus.Running, Attempts = node.Attempts + 1, Detail = null };
            var next = snapshot with { Nodes = nodes, UpdatedAt = DateTimeOffset.UtcNow, Version = snapshot.Version + 1 };
            if (await TryUpdateAndAuditAsync(snapshot, next, "node.started", step.StepId, null, ct).ConfigureAwait(false))
            {
                return next;
            }
            snapshot = await _store.LoadAsync(snapshot.Request.ExecutionId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Workflow checkpoint disappeared during node claim.");
        }
    }

    /// <summary>
    /// Delivers a decision for one waiting node of one execution, through the same version compare-and-swap and
    /// audit as every other transition. An approved <see cref="WorkflowStepKind.Approval"/> node succeeds; an
    /// approved node of any other kind returns to pending with the decision recorded, and the next handler call
    /// receives it in <see cref="DurableWorkflowNodeContext.Decision"/>. A rejection fails the node and the
    /// execution; when completed steps still have compensations to run, the execution stays open and
    /// <see cref="ResumeAsync"/> compensates, then fails it. After an approval the caller continues with <see cref="ResumeAsync"/>.
    /// </summary>
    /// <param name="executionId">The execution the node belongs to.</param>
    /// <param name="stepId">The waiting node.</param>
    /// <param name="approved">Approve or reject.</param>
    /// <param name="decidedBy">Identity of the decider.</param>
    /// <param name="reason">Optional reason or payload recorded with the decision.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">The execution or node is unknown, the execution is already terminal, or the node is not waiting.</exception>
    public async Task<DurableWorkflowSnapshot> DecideAsync(
        string executionId, string stepId, bool approved, string decidedBy, string? reason = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(decidedBy);

        var snapshot = await _store.LoadAsync(executionId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Execution '{executionId}' is unknown.");
        while (true)
        {
            if (IsTerminal(snapshot.Status))
                throw new InvalidOperationException($"Execution '{executionId}' is already {snapshot.Status}.");
            if (!snapshot.Nodes.TryGetValue(stepId, out var node))
                throw new InvalidOperationException($"Execution '{executionId}' has no node '{stepId}'.");
            if (node.Status != DurableWorkflowNodeStatus.Waiting)
                throw new InvalidOperationException($"Node '{stepId}' of execution '{executionId}' is {node.Status}, not Waiting.");

            var isApprovalKind = snapshot.Request.Workflow.Steps.Any(step => step.StepId == stepId && step.Kind == WorkflowStepKind.Approval);
            var decision = new DurableWorkflowDecision(approved, decidedBy, reason);
            var settled = !approved
                ? DurableWorkflowNodeStatus.Failed
                : isApprovalKind ? DurableWorkflowNodeStatus.Succeeded : DurableWorkflowNodeStatus.Pending;
            var detail = reason ?? (approved ? $"Approved by {decidedBy}." : $"Rejected by {decidedBy}.");
            var nodes = snapshot.Nodes.ToDictionary(pair => pair.Key, pair => pair.Value);
            nodes[stepId] = node with
            {
                Status = settled, Detail = detail, Decision = decision,
                CompletionOrder = settled == DurableWorkflowNodeStatus.Succeeded ? NextCompletionOrder(nodes) : node.CompletionOrder,
            };
            // A rejection fails the node and the execution in the same swap. The store appends one audit event per swap,
            // so node.rejected is written with the swap (its status already Failed) and execution.failed follows as a
            // plain append; a crash between the two leaves a consistent Failed checkpoint. When completed steps still
            // have compensations to run, the execution stays open instead: it fails, after compensating, on ResumeAsync.
            var failsNow = !approved && !NeedsCompensation(snapshot);
            var status = failsNow ? WorkflowExecutionStatus.Failed
                : snapshot.Status == WorkflowExecutionStatus.AwaitingConfirmation ? WorkflowExecutionStatus.Running : snapshot.Status;
            var next = snapshot with
            {
                Nodes = nodes, Status = status, FailureReason = approved ? snapshot.FailureReason : detail,
                UpdatedAt = DateTimeOffset.UtcNow, Version = snapshot.Version + 1,
            };
            if (await TryUpdateAndAuditAsync(snapshot, next, approved ? "node.approved" : "node.rejected", stepId, $"{decidedBy}: {detail}", ct).ConfigureAwait(false))
            {
                if (failsNow)
                    await _audit.AppendAsync(CreateAuditEvent(next, "execution.failed", null, detail), ct).ConfigureAwait(false);
                return next;
            }

            snapshot = await _store.LoadAsync(executionId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Workflow checkpoint disappeared during a decision.");
        }
    }

    /// <summary>
    /// Delivers a signal to the <see cref="WorkflowStepKind.WaitForSignal"/> steps of one execution that wait for
    /// <paramref name="signalName"/> (a step's <c>Inputs["signal"]</c>, or its step id when absent), through the same
    /// version compare-and-swap and audit as every other transition. A waiting step succeeds with the signal stored on
    /// its node. A step that is still pending keeps the signal and succeeds as soon as it is ready, without waiting.
    /// A step that already holds a signal takes no second one. After a signal the caller continues with
    /// <see cref="ResumeAsync"/>.
    /// </summary>
    /// <param name="executionId">The execution to signal.</param>
    /// <param name="signalName">The signal name.</param>
    /// <param name="payload">Optional payload, readable by dependent steps through <see cref="DurableWorkflowNodeContext.Dependencies"/>.</param>
    /// <param name="sentBy">Identity of the sender.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">The execution is unknown or already terminal, or no pending or waiting step accepts the signal.</exception>
    public async Task<DurableWorkflowSnapshot> SignalAsync(
        string executionId, string signalName, string? payload, string sentBy, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(signalName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sentBy);

        var snapshot = await _store.LoadAsync(executionId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Execution '{executionId}' is unknown.");
        while (true)
        {
            if (IsTerminal(snapshot.Status))
                throw new InvalidOperationException($"Execution '{executionId}' is already {snapshot.Status}.");
            var targets = snapshot.Request.Workflow.Steps
                .Where(step => step.Kind == WorkflowStepKind.WaitForSignal && SignalName(step) == signalName
                    && snapshot.Nodes[step.StepId] is { Status: DurableWorkflowNodeStatus.Waiting or DurableWorkflowNodeStatus.Pending, Signal: null })
                .ToList();
            if (targets.Count == 0)
                throw new InvalidOperationException($"Execution '{executionId}' has no pending or waiting WaitForSignal step that accepts signal '{signalName}'.");

            var signal = new DurableWorkflowSignal(signalName, payload, sentBy);
            var nodes = snapshot.Nodes.ToDictionary(pair => pair.Key, pair => pair.Value);
            foreach (var target in targets)
            {
                var node = nodes[target.StepId];
                nodes[target.StepId] = node.Status == DurableWorkflowNodeStatus.Waiting
                    ? node with { Status = DurableWorkflowNodeStatus.Succeeded, Signal = signal, Detail = $"Signal '{signalName}' from {sentBy}.", CompletionOrder = NextCompletionOrder(nodes) }
                    : node with { Signal = signal };
            }
            var status = snapshot.Status == WorkflowExecutionStatus.AwaitingConfirmation ? WorkflowExecutionStatus.Running : snapshot.Status;
            var next = snapshot with { Nodes = nodes, Status = status, UpdatedAt = DateTimeOffset.UtcNow, Version = snapshot.Version + 1 };
            var detail = $"Signal '{signalName}' from {sentBy} for {string.Join(", ", targets.Select(target => target.StepId))}.";
            if (await TryUpdateAndAuditAsync(snapshot, next, "signal.received", targets.Count == 1 ? targets[0].StepId : null, detail, ct).ConfigureAwait(false))
                return next;

            snapshot = await _store.LoadAsync(executionId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Workflow checkpoint disappeared during a signal.");
        }
    }

    static string SignalName(WorkflowStepDefinition step) =>
        step.Inputs is not null && step.Inputs.TryGetValue("signal", out var name) && !string.IsNullOrWhiteSpace(name) ? name : step.StepId;

    // Parks or settles a ready step that never reaches the handler. A lost compare-and-swap reloads and re-checks,
    // so a node settled by someone else in the meantime is left alone.
    Task<DurableWorkflowSnapshot> SettleNativeStepAsync(DurableWorkflowSnapshot snapshot, WorkflowStepDefinition step, CancellationToken ct)
    {
        switch (step.Kind)
        {
            case WorkflowStepKind.Approval:
                return ParkAsync(snapshot, step, $"Step '{step.StepId}' awaits approval.", ct);
            case WorkflowStepKind.WaitForSignal:
                // A signal that arrived early is handled by ParkAsync, which settles the step instead of parking it.
                return ParkAsync(snapshot, step, $"Step '{step.StepId}' awaits signal '{SignalName(step)}'.", ct);
            default:
                // The settling swap writes the event itself, so it exists exactly once, also across a crash and resume.
                var detail = step.Inputs!.GetValueOrDefault("detail");
                return SucceedNodeAsync(snapshot, step, $"event.{step.Inputs!["event"]}", detail, ct);
        }
    }

    async Task<DurableWorkflowSnapshot> ParkAsync(DurableWorkflowSnapshot snapshot, WorkflowStepDefinition step, string reason, CancellationToken ct)
    {
        while (true)
        {
            var node = snapshot.Nodes[step.StepId];
            if (node.Status != DurableWorkflowNodeStatus.Pending) return snapshot;
            // A signal can land between the decision to park and the swap, or while a lost swap reloads. A step that
            // holds its signal is settled, never parked: SignalAsync would not accept a second one.
            if (node.Signal is { } signal)
                return await SucceedNodeAsync(snapshot, step, "node.succeeded", $"Signal '{signal.Name}' from {signal.SentBy} arrived before the step was ready.", ct).ConfigureAwait(false);
            var nodes = snapshot.Nodes.ToDictionary(pair => pair.Key, pair => pair.Value);
            nodes[step.StepId] = node with { Status = DurableWorkflowNodeStatus.Waiting, Detail = reason, Decision = null };
            var next = snapshot with { Nodes = nodes, UpdatedAt = DateTimeOffset.UtcNow, Version = snapshot.Version + 1 };
            if (await TryUpdateAndAuditAsync(snapshot, next, "node.waiting", step.StepId, reason, ct).ConfigureAwait(false))
                return next;
            snapshot = await _store.LoadAsync(snapshot.Request.ExecutionId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Workflow checkpoint disappeared while parking a node.");
        }
    }

    async Task<DurableWorkflowSnapshot> SucceedNodeAsync(DurableWorkflowSnapshot snapshot, WorkflowStepDefinition step, string eventType, string? detail, CancellationToken ct)
    {
        while (true)
        {
            var node = snapshot.Nodes[step.StepId];
            if (node.Status != DurableWorkflowNodeStatus.Pending) return snapshot;
            var nodes = snapshot.Nodes.ToDictionary(pair => pair.Key, pair => pair.Value);
            nodes[step.StepId] = node with { Status = DurableWorkflowNodeStatus.Succeeded, Detail = detail, CompletionOrder = NextCompletionOrder(nodes) };
            var next = snapshot with { Nodes = nodes, UpdatedAt = DateTimeOffset.UtcNow, Version = snapshot.Version + 1 };
            if (await TryUpdateAndAuditAsync(snapshot, next, eventType, step.StepId, detail, ct).ConfigureAwait(false))
                return next;
            snapshot = await _store.LoadAsync(snapshot.Request.ExecutionId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Workflow checkpoint disappeared while settling a node.");
        }
    }

    static int NextCompletionOrder(IReadOnlyDictionary<string, DurableWorkflowNodeSnapshot> nodes) => nodes.Values.Max(node => node.CompletionOrder) + 1;

    // Succeeded steps that declare a compensation, newest completion first, each compensation once.
    static List<(WorkflowStepDefinition Completed, WorkflowStepDefinition Compensation)> CompensationPlan(DurableWorkflowSnapshot snapshot)
    {
        var steps = snapshot.Request.Workflow.Steps.ToDictionary(step => step.StepId, StringComparer.Ordinal);
        return snapshot.Request.Workflow.Steps
            .Where(step => step.CompensationStepId is not null && snapshot.Nodes[step.StepId].Status == DurableWorkflowNodeStatus.Succeeded)
            .OrderByDescending(step => snapshot.Nodes[step.StepId].CompletionOrder)
            .DistinctBy(step => step.CompensationStepId)
            .Select(step => (step, steps[step.CompensationStepId!]))
            .ToList();
    }

    static bool NeedsCompensation(DurableWorkflowSnapshot snapshot) =>
        CompensationPlan(snapshot).Any(entry => snapshot.Nodes[entry.Compensation.StepId].Status is DurableWorkflowNodeStatus.Pending or DurableWorkflowNodeStatus.Running);

    // Runs the compensations the execution still owes, then fails it. The cause stays the failure reason; a
    // compensation that failed is named after it. Done compensations are never run again, so a crash in the middle
    // is continued by ResumeAsync. Cancellation does not compensate.
    async Task<DurableWorkflowSnapshot> FailExecutionAsync(
        WorkflowExecutionRequest request,
        Func<DurableWorkflowNodeContext, CancellationToken, Task<DurableWorkflowNodeResult>> handler,
        DurableWorkflowSnapshot snapshot,
        string reason,
        CancellationToken ct)
    {
        foreach (var (completed, compensation) in CompensationPlan(snapshot))
        {
            while (snapshot.Nodes[compensation.StepId].Status == DurableWorkflowNodeStatus.Pending)
            {
                if (ct.IsCancellationRequested)
                    return await CancelAsync(snapshot, "Cancellation requested.", CancellationToken.None).ConfigureAwait(false);
                snapshot = await ClaimAsync(snapshot, compensation, ct).ConfigureAwait(false);
                if (snapshot.Nodes[compensation.StepId].Status != DurableWorkflowNodeStatus.Running) continue;

                var node = snapshot.Nodes[compensation.StepId];
                var context = new DurableWorkflowNodeContext(request, compensation, node.IdempotencyKey, node.Attempts)
                {
                    Dependencies = new Dictionary<string, DurableWorkflowNodeSnapshot> { [completed.StepId] = snapshot.Nodes[completed.StepId] },
                };
                var outcome = await InvokeHandlerAsync(handler, context, ct).ConfigureAwait(false);
                if (outcome is null || ct.IsCancellationRequested)
                    return await CancelAsync(snapshot, "Cancellation requested.", CancellationToken.None).ConfigureAwait(false);
                snapshot = await RecordOutcomeAsync(snapshot, compensation, outcome, null, ct).ConfigureAwait(false);
            }
        }

        var failed = CompensationPlan(snapshot)
            .Where(entry => snapshot.Nodes[entry.Compensation.StepId].Status == DurableWorkflowNodeStatus.Failed)
            .Select(entry => $"'{entry.Compensation.StepId}' ({snapshot.Nodes[entry.Compensation.StepId].Detail})")
            .ToList();
        var full = failed.Count == 0 ? reason : $"{reason} Compensation failed: {string.Join("; ", failed)}.";
        return await FailAsync(snapshot, full, ct).ConfigureAwait(false);
    }

    async Task<DurableWorkflowSnapshot> AwaitConfirmationAsync(DurableWorkflowSnapshot snapshot, CancellationToken ct)
    {
        var waiting = string.Join(", ", snapshot.Nodes.Values.Where(node => node.Status == DurableWorkflowNodeStatus.Waiting).Select(node => node.StepId));
        return await TransitionAsync(snapshot, WorkflowExecutionStatus.AwaitingConfirmation, null, "execution.awaiting_confirmation", ct, $"Waiting for a decision: {waiting}").ConfigureAwait(false);
    }

    async Task<DurableWorkflowSnapshot> RecoverInFlightNodesAsync(DurableWorkflowSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.Status != WorkflowExecutionStatus.Running || !snapshot.Nodes.Values.Any(node => node.Status == DurableWorkflowNodeStatus.Running)) return snapshot;
        var nodes = snapshot.Nodes.ToDictionary(pair => pair.Key, pair => pair.Value.Status == DurableWorkflowNodeStatus.Running
            ? pair.Value with { Status = DurableWorkflowNodeStatus.Pending, Detail = "Recovered after an interrupted attempt." }
            : pair.Value);
        var next = snapshot with { Nodes = nodes, UpdatedAt = DateTimeOffset.UtcNow, Version = snapshot.Version + 1 };
        if (!await TryUpdateAndAuditAsync(snapshot, next, "node.recovered", null, "Interrupted node attempts returned to pending with their original idempotency keys.", ct).ConfigureAwait(false))
            return await _store.LoadAsync(snapshot.Request.ExecutionId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Workflow checkpoint disappeared during recovery.");
        return next;
    }

    // The reason of the first failed step other than a compensation, or null. A failed compensation is not a cause:
    // it is recorded while the execution is already failing.
    static string? FailureCause(DurableWorkflowSnapshot snapshot, IReadOnlySet<string> compensateIds)
    {
        var failedNode = snapshot.Nodes.Values.FirstOrDefault(node => node.Status == DurableWorkflowNodeStatus.Failed && !compensateIds.Contains(node.StepId));
        return failedNode is null ? null : failedNode.Detail ?? $"Step '{failedNode.StepId}' failed.";
    }

    // Records one outcome on the node. It never fails the execution: the caller does that once the whole batch is recorded.
    async Task<DurableWorkflowSnapshot> RecordOutcomeAsync(
        DurableWorkflowSnapshot snapshot,
        WorkflowStepDefinition step,
        DurableWorkflowNodeResult outcome,
        Evaluation? evaluation,
        CancellationToken ct)
    {
        var stepId = step.StepId;
        // A lost swap (a decision, a sibling's outcome or another instance wrote first) reloads and applies the same
        // outcome to the fresh checkpoint, using the fresh node. It stops only when the node is no longer Running,
        // meaning someone else already settled it.
        while (true)
        {
            var node = snapshot.Nodes[stepId];
            if (node.Status != DurableWorkflowNodeStatus.Running) return snapshot;
            var applied = await TryApplyOutcomeAsync(snapshot, node, step, outcome, evaluation, ct).ConfigureAwait(false);
            if (applied is not null) return applied;
            snapshot = await _store.LoadAsync(snapshot.Request.ExecutionId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Workflow checkpoint disappeared during node completion.");
        }
    }

    // Returns the resulting checkpoint, or null when the swap was lost.
    async Task<DurableWorkflowSnapshot?> TryApplyOutcomeAsync(
        DurableWorkflowSnapshot snapshot,
        DurableWorkflowNodeSnapshot node,
        WorkflowStepDefinition step,
        DurableWorkflowNodeResult outcome,
        Evaluation? evaluation,
        CancellationToken ct)
    {
        var stepId = step.StepId;
        // Loop iterations are tracked independently from Attempts: Attempts increments on every claim
        // (including retries of a failed attempt), so bounding loop re-execution by Attempts would let
        // retries silently eat into the caller's declared MaxIterations budget.
        var loopIterations = step.Kind == WorkflowStepKind.Loop && outcome.Succeeded ? node.LoopIterations + 1 : node.LoopIterations;
        var loopAgain = outcome.Succeeded && step.Kind == WorkflowStepKind.Loop && outcome.LoopContinue
            && step.MaxIterations is { } maxIterations && loopIterations < maxIterations;
        var retry = !outcome.Succeeded && outcome.Retryable && node.Attempts < _maxAttempts;
        var status = outcome.Waiting
            ? DurableWorkflowNodeStatus.Waiting
            : outcome.Succeeded
                ? loopAgain ? DurableWorkflowNodeStatus.Pending : DurableWorkflowNodeStatus.Succeeded
                : retry ? DurableWorkflowNodeStatus.Pending : DurableWorkflowNodeStatus.Failed;
        var nodes = snapshot.Nodes.ToDictionary(pair => pair.Key, pair => pair.Value);
        // A node that waits again, or starts another loop iteration, asks for a new decision, so the one it holds is dropped.
        nodes[stepId] = node with
        {
            Status = status, Detail = outcome.Detail, LoopIterations = loopIterations,
            Decision = outcome.Waiting || loopAgain ? null : node.Decision,
            Observations = evaluation?.Observations ?? node.Observations,
        };

        if (outcome.Succeeded && !loopAgain && step.Kind == WorkflowStepKind.Branch)
            ApplyBranchOutcome(snapshot.Request.Workflow, step, outcome.SelectedNextStepId, nodes);

        // Compute the audit event type from the node's FINAL settled status (post branch resolution),
        // not from the raw handler outcome -- otherwise a branch step whose selection ApplyBranchOutcome
        // rejects gets persisted as Failed while the emitted audit event still claims "node.succeeded".
        var finalStatus = nodes[stepId].Status;
        if (finalStatus == DurableWorkflowNodeStatus.Succeeded)
            nodes[stepId] = nodes[stepId] with { CompletionOrder = NextCompletionOrder(nodes) };
        var eventType = finalStatus switch
        {
            DurableWorkflowNodeStatus.Succeeded => "node.succeeded",
            DurableWorkflowNodeStatus.Pending => loopAgain ? "node.loop.iterated" : "node.retrying",
            DurableWorkflowNodeStatus.Failed => "node.failed",
            DurableWorkflowNodeStatus.Waiting => "node.waiting",
            _ => throw new InvalidOperationException($"Step '{stepId}' completed with unexpected status '{finalStatus}'."),
        };
        var next = snapshot with { Nodes = nodes, UpdatedAt = DateTimeOffset.UtcNow, Version = snapshot.Version + 1 };
        // An evaluation is one audit event written by the swap that stores the observations: every metric with its
        // value and threshold result, so on a transactional store it is atomic with the node.
        var swapped = evaluation is null
            ? await TryUpdateAndAuditAsync(snapshot, next, eventType, stepId, nodes[stepId].Detail, ct).ConfigureAwait(false)
            : await TryUpdateAndAuditAsync(snapshot, next, "node.evaluated", stepId, evaluation.Summary, ct).ConfigureAwait(false);
        return swapped ? next : null;
    }

    /// <summary>
    /// Marks every unselected declared branch target — and any of its transitive, still-pending dependents — as
    /// <see cref="DurableWorkflowNodeStatus.Skipped"/> so only the selected branch executes. If the handler did not
    /// select one of the step's declared targets, the branch step itself is marked failed.
    /// </summary>
    static void ApplyBranchOutcome(WorkflowDefinition workflow, WorkflowStepDefinition branchStep, string? selected, Dictionary<string, DurableWorkflowNodeSnapshot> nodes)
    {
        var targets = branchStep.BranchTargets ?? [];
        if (selected is null || !targets.Contains(selected, StringComparer.Ordinal))
        {
            nodes[branchStep.StepId] = nodes[branchStep.StepId] with
            {
                Status = DurableWorkflowNodeStatus.Failed,
                Detail = $"Branch step '{branchStep.StepId}' did not select one of its declared branch targets.",
            };
            return;
        }

        var skipped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in targets)
            if (!string.Equals(target, selected, StringComparison.Ordinal))
                skipped.Add(target);

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var candidate in workflow.Steps)
            {
                if (skipped.Contains(candidate.StepId)) continue;
                if (nodes[candidate.StepId].Status != DurableWorkflowNodeStatus.Pending) continue;
                // A Join step reconverges its declared dependencies rather than requiring all of them: it is
                // only unreachable (and therefore cascade-skipped) when EVERY one of its dependencies is
                // skipped. A Sequential/Tool/etc. step, by contrast, is cascade-skipped as soon as ANY one of
                // its dependencies is skipped, since it has no reconvergence semantics of its own. Getting
                // this wrong for Join specifically was a real bug: with the `Any` rule applied uniformly, a
                // Join depending on a Branch's unselected target would itself be skip-cascaded before it ever
                // got a chance to run -- the whole execution would then report Succeeded without the Join (or
                // anything downstream of it) ever executing.
                var cascadeSkip = candidate.Kind == WorkflowStepKind.Join
                    ? candidate.DependsOn.Count > 0 && candidate.DependsOn.All(skipped.Contains)
                    : candidate.DependsOn.Any(skipped.Contains);
                if (cascadeSkip)
                {
                    skipped.Add(candidate.StepId);
                    changed = true;
                }
            }
        }

        foreach (var stepId in skipped)
            nodes[stepId] = nodes[stepId] with
            {
                Status = DurableWorkflowNodeStatus.Skipped,
                Detail = $"Skipped: not reachable from the branch selected by '{branchStep.StepId}'.",
            };
    }

    // Compensations that were never needed end Skipped in the same swap that succeeds the execution.
    Task<DurableWorkflowSnapshot> SucceedAsync(DurableWorkflowSnapshot snapshot, IReadOnlySet<string> compensateIds, CancellationToken ct) =>
        TransitionAsync(snapshot, WorkflowExecutionStatus.Succeeded, null, "execution.succeeded", ct, skipPending: compensateIds);
    Task<DurableWorkflowSnapshot> FailAsync(DurableWorkflowSnapshot snapshot, string reason, CancellationToken ct) => TransitionAsync(snapshot, WorkflowExecutionStatus.Failed, reason, "execution.failed", ct, retryOnLoss: true);
    Task<DurableWorkflowSnapshot> CancelAsync(DurableWorkflowSnapshot snapshot, string reason, CancellationToken ct) => TransitionAsync(snapshot, WorkflowExecutionStatus.Cancelled, reason, "execution.cancelled", ct, retryOnLoss: true);

    // Failing and cancelling are one-way intentions, so a lost swap reloads and applies them to the fresh checkpoint
    // unless that one is already terminal. Succeeding and awaiting depend on the state, so a lost swap hands the fresh
    // checkpoint back and the caller scans it again.
    async Task<DurableWorkflowSnapshot> TransitionAsync(DurableWorkflowSnapshot snapshot, WorkflowExecutionStatus status, string? reason, string eventType, CancellationToken ct, string? detail = null, bool retryOnLoss = false, IReadOnlySet<string>? skipPending = null)
    {
        while (true)
        {
            var nodes = skipPending is null
                ? snapshot.Nodes
                : snapshot.Nodes.ToDictionary(pair => pair.Key, pair => skipPending.Contains(pair.Key) && pair.Value.Status == DurableWorkflowNodeStatus.Pending
                    ? pair.Value with { Status = DurableWorkflowNodeStatus.Skipped, Detail = "Skipped: the execution succeeded without compensating." }
                    : pair.Value);
            var next = snapshot with { Nodes = nodes, Status = status, FailureReason = reason, UpdatedAt = DateTimeOffset.UtcNow, Version = snapshot.Version + 1 };
            if (await TryUpdateAndAuditAsync(snapshot, next, eventType, null, detail ?? reason, ct).ConfigureAwait(false))
                return next;
            snapshot = await _store.LoadAsync(snapshot.Request.ExecutionId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Workflow checkpoint disappeared during terminal transition.");
            if (!retryOnLoss || IsTerminal(snapshot.Status)) return snapshot;
        }
    }

    async Task<bool> TryCreateAndAuditAsync(DurableWorkflowSnapshot snapshot, string eventType, string? stepId, string? detail, CancellationToken ct)
    {
        var auditEvent = CreateAuditEvent(snapshot, eventType, stepId, detail);
        if (AtomicPersistence is { } persistence)
            return await persistence.TryCreateAndAppendAsync(snapshot, auditEvent, ct).ConfigureAwait(false);
        if (!await _store.TryCreateAsync(snapshot, ct).ConfigureAwait(false)) return false;
        await _audit.AppendAsync(auditEvent, ct).ConfigureAwait(false);
        return true;
    }

    async Task<bool> TryUpdateAndAuditAsync(DurableWorkflowSnapshot expected, DurableWorkflowSnapshot next, string eventType, string? stepId, string? detail, CancellationToken ct)
    {
        var auditEvent = CreateAuditEvent(next, eventType, stepId, detail);
        if (AtomicPersistence is { } persistence)
            return await persistence.TryUpdateAndAppendAsync(expected.Request.ExecutionId, expected, next, auditEvent, ct).ConfigureAwait(false);
        if (!await _store.TryUpdateAsync(expected.Request.ExecutionId, expected, next, ct).ConfigureAwait(false)) return false;
        await _audit.AppendAsync(auditEvent, ct).ConfigureAwait(false);
        return true;
    }

    IWorkflowCheckpointTransactionStore? AtomicPersistence => _store is IWorkflowCheckpointTransactionStore persistence && ReferenceEquals(persistence, _audit) ? persistence : null;

    WorkflowRuntimeAuditEvent CreateAuditEvent(DurableWorkflowSnapshot snapshot, string eventType, string? stepId, string? detail) =>
        new(Interlocked.Increment(ref _sequence), snapshot.Request.ExecutionId, eventType, stepId, snapshot.Status, DateTimeOffset.UtcNow, detail);

    Task AuditAsync(DurableWorkflowSnapshot snapshot, string eventType, string? stepId, string? detail, CancellationToken ct) =>
        _audit.AppendAsync(new WorkflowRuntimeAuditEvent(Interlocked.Increment(ref _sequence), snapshot.Request.ExecutionId, eventType, stepId, snapshot.Status, DateTimeOffset.UtcNow, detail), ct);

    // Uses TryGetValue rather than the dictionary indexer: a ResumeAsync call whose request.Workflow declares
    // steps or dependencies absent from the persisted checkpoint's node set (i.e. the definition changed since
    // the execution was created) must not crash with a KeyNotFoundException. A step or dependency missing from
    // the persisted node set is treated as not-ready rather than thrown.
    static bool IsReady(WorkflowStepDefinition step, IReadOnlyDictionary<string, DurableWorkflowNodeSnapshot> nodes)
    {
        if (!nodes.TryGetValue(step.StepId, out var self) || self.Status != DurableWorkflowNodeStatus.Pending) return false;
        // A compensation runs only when the execution fails, never as part of the forward flow.
        if (step.Kind == WorkflowStepKind.Compensate) return false;

        // A Join step reconverges: it is ready once every dependency has settled (Succeeded or Skipped -- not
        // still Pending/Running/Failed) AND at least one of them actually Succeeded. A dependency reaching
        // Skipped alone never satisfies readiness -- if every one of a Join's dependencies ends up Skipped,
        // ApplyBranchOutcome's cascade separately marks the Join itself Skipped, so this method never needs to
        // decide "ready" for that case.
        if (step.Kind == WorkflowStepKind.Join)
        {
            var anySucceeded = false;
            foreach (var dependency in step.DependsOn)
            {
                if (!nodes.TryGetValue(dependency, out var dependencyNode)) return false;
                if (dependencyNode.Status == DurableWorkflowNodeStatus.Succeeded) anySucceeded = true;
                else if (dependencyNode.Status != DurableWorkflowNodeStatus.Skipped) return false;
            }
            return anySucceeded;
        }

        return step.DependsOn.All(dependency => nodes.TryGetValue(dependency, out var dependencyNode) && dependencyNode.Status == DurableWorkflowNodeStatus.Succeeded);
    }

    static bool IsTerminal(WorkflowExecutionStatus status) => status is WorkflowExecutionStatus.Succeeded or WorkflowExecutionStatus.Failed or WorkflowExecutionStatus.Cancelled;

    void ValidateDefinition(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Steps.Count == 0) throw new ArgumentException("Workflow must contain at least one step.", nameof(definition));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in definition.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.StepId) || !ids.Add(step.StepId)) throw new ArgumentException("Workflow step identifiers must be unique and non-empty.", nameof(definition));
        }
        foreach (var step in definition.Steps)
        {
            if (!Enum.IsDefined(step.Kind))
                throw new ArgumentException($"Step '{step.StepId}' declares an unknown step kind '{step.Kind}'.", nameof(definition));

            // A kind whose run needs something the definition or the runtime does not supply must fail loud here
            // rather than park forever or fail at its first attempt.
            if (step.Kind == WorkflowStepKind.Evaluate)
            {
                if (definition.EvaluationPlan is not { Enabled: true })
                    throw new ArgumentException($"Evaluate step '{step.StepId}' needs a workflow EvaluationPlan that is enabled.", nameof(definition));
                if (_evaluator is null)
                    throw new ArgumentException($"Evaluate step '{step.StepId}' needs an evaluator, and this runtime has none.", nameof(definition));
            }

            if (step.Kind == WorkflowStepKind.EmitEvent && (step.Inputs is null || !step.Inputs.TryGetValue("event", out var eventName) || string.IsNullOrWhiteSpace(eventName)))
                throw new ArgumentException($"EmitEvent step '{step.StepId}' must declare a non-blank 'event' input.", nameof(definition));

            if (step.CompensationStepId is { } compensationId)
            {
                var target = definition.Steps.FirstOrDefault(candidate => candidate.StepId == compensationId);
                if (target is null)
                    throw new ArgumentException($"Step '{step.StepId}' names compensation step '{compensationId}', which does not exist.", nameof(definition));
                if (target.Kind != WorkflowStepKind.Compensate)
                    throw new ArgumentException($"Step '{step.StepId}' names compensation step '{compensationId}', which is of kind '{target.Kind}', not Compensate.", nameof(definition));
            }

            if (step.Kind == WorkflowStepKind.Compensate && !definition.Steps.Any(candidate => candidate.CompensationStepId == step.StepId))
                throw new ArgumentException($"Compensate step '{step.StepId}' is not named by any step's CompensationStepId.", nameof(definition));

            foreach (var dependency in step.DependsOn)
                if (!ids.Contains(dependency)) throw new ArgumentException($"Step '{step.StepId}' depends on unknown step '{dependency}'.", nameof(definition));

            if (step.Kind == WorkflowStepKind.Branch)
            {
                if (step.BranchTargets is null || step.BranchTargets.Count == 0)
                    throw new ArgumentException($"Branch step '{step.StepId}' must declare at least one branch target.", nameof(definition));
                foreach (var target in step.BranchTargets)
                    if (!ids.Contains(target))
                        throw new ArgumentException($"Branch step '{step.StepId}' targets unknown step '{target}'.", nameof(definition));
            }

            if (step.Kind == WorkflowStepKind.Loop && step.MaxIterations is not (> 0))
                throw new ArgumentException($"Loop step '{step.StepId}' must declare a positive MaxIterations bound.", nameof(definition));
        }
    }
}

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.Agent.Contracts;

namespace SignalGrid.Ampelum.Agent.Orchestration;

/// <summary>Durable state of a single declarative workflow node.</summary>
public enum DurableWorkflowNodeStatus { Pending, Running, Succeeded, Failed, Cancelled, Skipped }

/// <summary>Outcome returned by a durable workflow node handler.</summary>
/// <param name="Succeeded">Whether the node completed successfully.</param>
/// <param name="Retryable">Whether a failed outcome should be retried, up to the runtime's configured maximum attempts.</param>
/// <param name="Detail">Optional human-readable detail recorded alongside the outcome.</param>
/// <param name="SelectedNextStepId">For a <see cref="WorkflowStepKind.Branch"/> step, the declared branch target this outcome selects.</param>
/// <param name="LoopContinue">For a <see cref="WorkflowStepKind.Loop"/> step, whether the loop body should re-execute (subject to the step's <c>MaxIterations</c> bound).</param>
public sealed record DurableWorkflowNodeResult(
    bool Succeeded,
    bool Retryable = false,
    string? Detail = null,
    string? SelectedNextStepId = null,
    bool LoopContinue = false)
{
    /// <summary>Creates a successful node outcome.</summary>
    public static DurableWorkflowNodeResult Success(string? detail = null) => new(true, false, detail);

    /// <summary>Creates a failed node outcome.</summary>
    public static DurableWorkflowNodeResult Failure(string? detail = null, bool retryable = false) => new(false, retryable, detail);
}

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
public sealed record DurableWorkflowNodeSnapshot(
    string StepId,
    DurableWorkflowNodeStatus Status,
    int Attempts,
    string IdempotencyKey,
    string? Detail = null,
    int LoopIterations = 0);

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
    int Attempt);

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
    public DurableWorkflowRuntime(IWorkflowStateStore store, IWorkflowRuntimeAuditSink audit, int maxAttempts = 3, int maxConcurrency = 8)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        if (maxConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        _maxAttempts = maxAttempts;
        _maxConcurrency = maxConcurrency;
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

            // Dispatch the WHOLE ready frontier concurrently, not just the first ready step: a DAG with
            // several simultaneously-ready independent branches (e.g. downstream of a FanOut, or simply
            // several roots with no DependsOn) previously executed strictly one node at a time even though
            // nothing coupled them. Bounded by _maxConcurrency below.
            var readySteps = request.Workflow.Steps.Where(candidate => IsReady(candidate, snapshot.Nodes)).ToList();
            if (readySteps.Count == 0)
            {
                if (snapshot.Nodes.Values.Any(node => node.Status == DurableWorkflowNodeStatus.Running))
                    return snapshot;
                var incomplete = snapshot.Nodes.Values.Any(node => node.Status is DurableWorkflowNodeStatus.Pending or DurableWorkflowNodeStatus.Running);
                if (incomplete)
                    return await FailAsync(snapshot, "Workflow contains unresolved dependencies.", ct).ConfigureAwait(false);
                return await SucceedAsync(snapshot, ct).ConfigureAwait(false);
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

            foreach (var (step, outcome) in outcomes)
                snapshot = await RecordOutcomeAsync(snapshot, step, outcome, ct).ConfigureAwait(false);
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
    async Task<(WorkflowStepDefinition Step, DurableWorkflowNodeResult Outcome)[]> DispatchClaimedBatchAsync(
        WorkflowExecutionRequest request,
        DurableWorkflowSnapshot snapshot,
        IReadOnlyList<WorkflowStepDefinition> claimedSteps,
        Func<DurableWorkflowNodeContext, CancellationToken, Task<DurableWorkflowNodeResult>> handler,
        CancellationToken ct)
    {
        using var throttle = new SemaphoreSlim(Math.Min(_maxConcurrency, claimedSteps.Count));
        var results = await Task.WhenAll(claimedSteps.Select(step => DispatchOneAsync(step))).ConfigureAwait(false);
        return [.. results.Where(result => result.Outcome is not null).Select(result => (result.Step, result.Outcome!))];

        async Task<(WorkflowStepDefinition Step, DurableWorkflowNodeResult? Outcome)> DispatchOneAsync(WorkflowStepDefinition step)
        {
            await throttle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var node = snapshot.Nodes[step.StepId];
                try
                {
                    var outcome = await handler(new DurableWorkflowNodeContext(request, step, node.IdempotencyKey, node.Attempts), ct).ConfigureAwait(false);
                    return (step, outcome);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return (step, null);
                }
                catch (Exception ex)
                {
                    return (step, DurableWorkflowNodeResult.Failure(ex.Message, retryable: false));
                }
            }
            finally
            {
                throttle.Release();
            }
        }
    }

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

    async Task<DurableWorkflowSnapshot> RecordOutcomeAsync(DurableWorkflowSnapshot snapshot, WorkflowStepDefinition step, DurableWorkflowNodeResult outcome, CancellationToken ct)
    {
        var stepId = step.StepId;
        var node = snapshot.Nodes[stepId];
        // Loop iterations are tracked independently from Attempts: Attempts increments on every claim
        // (including retries of a failed attempt), so bounding loop re-execution by Attempts would let
        // retries silently eat into the caller's declared MaxIterations budget.
        var loopIterations = step.Kind == WorkflowStepKind.Loop && outcome.Succeeded ? node.LoopIterations + 1 : node.LoopIterations;
        var loopAgain = outcome.Succeeded && step.Kind == WorkflowStepKind.Loop && outcome.LoopContinue
            && step.MaxIterations is { } maxIterations && loopIterations < maxIterations;
        var retry = !outcome.Succeeded && outcome.Retryable && node.Attempts < _maxAttempts;
        var status = outcome.Succeeded
            ? loopAgain ? DurableWorkflowNodeStatus.Pending : DurableWorkflowNodeStatus.Succeeded
            : retry ? DurableWorkflowNodeStatus.Pending : DurableWorkflowNodeStatus.Failed;
        var nodes = snapshot.Nodes.ToDictionary(pair => pair.Key, pair => pair.Value);
        nodes[stepId] = node with { Status = status, Detail = outcome.Detail, LoopIterations = loopIterations };

        if (outcome.Succeeded && !loopAgain && step.Kind == WorkflowStepKind.Branch)
            ApplyBranchOutcome(snapshot.Request.Workflow, step, outcome.SelectedNextStepId, nodes);

        // Compute the audit event type from the node's FINAL settled status (post branch resolution),
        // not from the raw handler outcome -- otherwise a branch step whose selection ApplyBranchOutcome
        // rejects gets persisted as Failed while the emitted audit event still claims "node.succeeded".
        var finalStatus = nodes[stepId].Status;
        var eventType = finalStatus switch
        {
            DurableWorkflowNodeStatus.Succeeded => "node.succeeded",
            DurableWorkflowNodeStatus.Pending => loopAgain ? "node.loop.iterated" : "node.retrying",
            DurableWorkflowNodeStatus.Failed => "node.failed",
            _ => throw new InvalidOperationException($"Step '{stepId}' completed with unexpected status '{finalStatus}'."),
        };
        var next = snapshot with { Nodes = nodes, UpdatedAt = DateTimeOffset.UtcNow, Version = snapshot.Version + 1 };
        if (!await TryUpdateAndAuditAsync(snapshot, next, eventType, stepId, nodes[stepId].Detail, ct).ConfigureAwait(false))
            return await _store.LoadAsync(snapshot.Request.ExecutionId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Workflow checkpoint disappeared during node completion.");
        var failed = finalStatus == DurableWorkflowNodeStatus.Failed;
        if (failed)
            return await FailAsync(next, nodes[stepId].Detail ?? $"Step '{stepId}' failed.", ct).ConfigureAwait(false);
        return next;
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

    Task<DurableWorkflowSnapshot> SucceedAsync(DurableWorkflowSnapshot snapshot, CancellationToken ct) => TransitionAsync(snapshot, WorkflowExecutionStatus.Succeeded, null, "execution.succeeded", ct);
    Task<DurableWorkflowSnapshot> FailAsync(DurableWorkflowSnapshot snapshot, string reason, CancellationToken ct) => TransitionAsync(snapshot, WorkflowExecutionStatus.Failed, reason, "execution.failed", ct);
    Task<DurableWorkflowSnapshot> CancelAsync(DurableWorkflowSnapshot snapshot, string reason, CancellationToken ct) => TransitionAsync(snapshot, WorkflowExecutionStatus.Cancelled, reason, "execution.cancelled", ct);

    async Task<DurableWorkflowSnapshot> TransitionAsync(DurableWorkflowSnapshot snapshot, WorkflowExecutionStatus status, string? reason, string eventType, CancellationToken ct)
    {
        var next = snapshot with { Status = status, FailureReason = reason, UpdatedAt = DateTimeOffset.UtcNow, Version = snapshot.Version + 1 };
        if (!await TryUpdateAndAuditAsync(snapshot, next, eventType, null, reason, ct).ConfigureAwait(false))
            return await _store.LoadAsync(snapshot.Request.ExecutionId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Workflow checkpoint disappeared during terminal transition.");
        return next;
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

    // FanOut/Join lifted (WS3): both dispatch through the existing generic handler path exactly like
    // Sequential/Tool -- FanOut's "parallelism" is the handler's own concern (it may internally await
    // several concurrent sub-tasks), and Join's "reconvergence" falls out of the existing DependsOn
    // readiness check for free once its declared dependency (conventionally the FanOut step) succeeds.
    // Neither needed a claim-loop change. WaitForSignal/Evaluate/EmitEvent/Compensate each need genuine new
    // runtime primitives (durable external signal delivery, evaluation-plan execution, custom event
    // emission, saga-compensation triggering) and remain rejected until each is implemented.
    static readonly HashSet<WorkflowStepKind> NotYetExecutableKinds =
    [
        WorkflowStepKind.WaitForSignal,
        WorkflowStepKind.Evaluate,
        WorkflowStepKind.EmitEvent,
        WorkflowStepKind.Compensate,
    ];

    static void ValidateDefinition(WorkflowDefinition definition)
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

            // FanOut/Join/WaitForSignal/Evaluate/EmitEvent/Compensate exist in the AST (WorkflowStepKind) so a
            // compiler can describe them, but DurableWorkflowRuntime does not yet implement their execution
            // semantics. Declaring one here must fail loud at validation time rather than silently execute as
            // a plain Sequential step and report false success -- the same class of defect as a placeholder
            // "always succeeds" executor. Each rejection is lifted only once the runtime implements that kind.
            if (NotYetExecutableKinds.Contains(step.Kind))
                throw new ArgumentException($"Step '{step.StepId}' declares kind '{step.Kind}', which DurableWorkflowRuntime does not yet execute.", nameof(definition));

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

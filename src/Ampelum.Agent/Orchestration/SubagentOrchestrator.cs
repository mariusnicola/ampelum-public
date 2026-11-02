using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SignalGrid.Ampelum.Agent;

/// <summary>Orchestrates parallel execution of tasks from a dependency DAG.
/// Independent tasks run concurrently; dependent tasks wait.
/// Uses TaskNode/TaskExecutionResult (string-based TaskType).
/// Also supports typed executors (ITypedTaskExecutor) via the ExecuteTypedAsync overload.</summary>
public sealed class SubagentOrchestrator
{
    private readonly ILogger _logger;
    private readonly IReadOnlyList<ITypedTaskExecutor>? _typedExecutors;
    private readonly OrchestratorOptions _options;

    /// <summary>Create an orchestrator with the legacy callback-based API.</summary>
    public SubagentOrchestrator(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _typedExecutors = null;
        _options = new OrchestratorOptions();
    }

    /// <summary>Create an orchestrator with typed task executors.</summary>
    public SubagentOrchestrator(
        IReadOnlyList<ITypedTaskExecutor> executors,
        OrchestratorOptions? options = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(executors);
        _typedExecutors = executors;
        _options = options ?? new OrchestratorOptions();
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Execute all tasks respecting the DAG. Returns results in topological order.
    /// Failed parents cause dependent children to be skipped.
    /// Deadlock detection: if no tasks are ready but some remain, throws.</summary>
    public async Task<IReadOnlyList<TaskExecutionResult>> ExecuteAsync(
        IReadOnlyList<TaskNode> nodes,
        Func<TaskNode, CancellationToken, Task<TaskExecutionResult>> executor,
        CancellationToken ct = default)
    {
        var results = new ConcurrentDictionary<string, TaskExecutionResult>();
        var errors = new ConcurrentBag<Exception>();
        var nodeMap = nodes.ToDictionary(n => n.Id);
        var remaining = new HashSet<string>(nodes.Select(n => n.Id));

        async Task RunNode(TaskNode node)
        {
            try
            {
                _logger.LogDebug("Starting node {NodeId} '{Description}'", node.Id, node.Description);
                var result = await executor(node, ct);
                results[node.Id] = result;
                _logger.LogDebug("Completed node {NodeId}: Success={Success}", node.Id, result.Success);
            }
            catch (OperationCanceledException)
            {
                results[node.Id] = new TaskExecutionResult
                {
                    Success = false,
                    Error = "cancelled",
                };
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Node {NodeId} '{Description}' failed", node.Id, node.Description);
                results[node.Id] = new TaskExecutionResult
                {
                    Success = false,
                    Error = ex.Message,
                };
                errors.Add(ex);
            }
        }

        while (remaining.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            // Find nodes whose dependencies are all completed
            var ready = remaining
                .Select(id => nodeMap[id])
                .Where(n => n.DependsOn.All(d =>
                    results.TryGetValue(d, out var dep) && dep.Success))
                .ToList();

            if (ready.Count == 0)
            {
                // Check for deadlocks — nodes where a dependency failed
                var blocked = remaining
                    .Select(id => nodeMap[id])
                    .Where(n => n.DependsOn.Any(d =>
                        results.TryGetValue(d, out var dep) && !dep.Success))
                    .ToList();

                foreach (var b in blocked)
                {
                    results[b.Id] = new TaskExecutionResult
                    {
                        Success = false,
                        Error = "blocked by failed dependency",
                    };
                    remaining.Remove(b.Id);
                    _logger.LogWarning("Node {NodeId} skipped — blocked by failed dependency", b.Id);
                }

                if (remaining.Count > 0 && ready.Count == 0)
                    throw new InvalidOperationException(
                        $"Deadlock: {remaining.Count} nodes remain but none are ready. " +
                        $"Check for missing dependencies in: {string.Join(", ", remaining)}.");

                break;
            }

            // Run all ready nodes in parallel
            var batch = ready.Select(n => RunNode(n));
            await Task.WhenAll(batch);

            foreach (var r in ready) remaining.Remove(r.Id);
        }

        if (!errors.IsEmpty)
        {
            _logger.LogWarning("{Count} node(s) failed during execution", errors.Count);
        }

        // Return in topological order (by node ID)
        return nodes
            .Select(n => results.TryGetValue(n.Id, out var r)
                ? r
                : new TaskExecutionResult { Success = false, Error = "not executed" })
            .ToList();
    }

    /// <summary>
    /// Execute a DAG of typed TaskDefinitions using registered ITypedTaskExecutor instances.
    /// Enforces DAG structure at construction time: rejects unknown dependencies and circular references.
    /// </summary>
    public async Task<OrchestratorResult> ExecuteTypedAsync(
        IReadOnlyList<TaskDefinition> dag,
        AgentIdentity callerIdentity,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dag);
        ArgumentNullException.ThrowIfNull(callerIdentity);
        if (_typedExecutors is null) throw new InvalidOperationException("Use the typed executor constructor.");

        // Validate DAG structure
        ValidateDag(dag);

        var startTime = DateTimeOffset.UtcNow;
        var results = new Dictionary<string, TaskResult>();
        var failed = new List<string>();
        var skipped = new List<string>();
        var remaining = new HashSet<string>(dag.Select(t => t.Id));
        var taskMap = dag.ToDictionary(t => t.Id);

        while (remaining.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            // Find tasks whose dependencies have all succeeded
            var ready = remaining
                .Select(id => taskMap[id])
                .Where(t => t.DependsOn.All(dep =>
                    results.TryGetValue(dep, out var r) && r.Success))
                .ToList();

            // Mark tasks whose dependencies failed as skipped
            var nowSkipped = remaining
                .Select(id => taskMap[id])
                .Where(t => t.DependsOn.Any(dep =>
                    failed.Contains(dep) || skipped.Contains(dep)))
                .ToList();

            foreach (var task in nowSkipped)
            {
                _logger.LogWarning("Task {TaskId} skipped — dependency failed", task.Id);
                skipped.Add(task.Id);
                remaining.Remove(task.Id);
            }

            if (ready.Count == 0 && remaining.Count > 0)
            {
                // Deadlock: remaining tasks are all blocked
                _logger.LogError("Deadlock: {Count} tasks remain but none are ready", remaining.Count);
                break;
            }

            // Execute ready tasks in parallel (limited by MaxParallelism)
            var batch = ready.Take(_options.MaxParallelism).ToList();
            var tasks = batch.Select(task => ExecuteOneAsync(task, callerIdentity, ct));
            var batchResults = await Task.WhenAll(tasks).ConfigureAwait(false);

            for (int i = 0; i < batch.Count; i++)
            {
                var result = batchResults[i];
                results[batch[i].Id] = result;
                if (!result.Success) failed.Add(batch[i].Id);
                remaining.Remove(batch[i].Id);
            }
        }

        var totalDuration = DateTimeOffset.UtcNow - startTime;
        // Return results in DAG order (matching the input order)
        var allResults = dag
            .Select(task => results.TryGetValue(task.Id, out var r)
                ? r
                : new TaskResult(task.Id, false, null, "not executed", new Dictionary<string, object?>(), TimeSpan.Zero))
            .ToList();
        return new OrchestratorResult(
            Success: failed.Count == 0,
            Results: allResults,
            FailedTaskIds: failed,
            SkippedTaskIds: skipped,
            TotalDuration: totalDuration
        );
    }

    /// <summary>Execute a single typed task by finding and invoking its executor.</summary>
    private async Task<TaskResult> ExecuteOneAsync(
        TaskDefinition task,
        AgentIdentity caller,
        CancellationToken ct)
    {
        var start = DateTimeOffset.UtcNow;
        var executor = _typedExecutors!.FirstOrDefault(e => e.TaskType == task.TaskType);

        if (executor is null)
        {
            _logger.LogError("No executor found for task type '{TaskType}'", task.TaskType);
            return new TaskResult(
                task.Id,
                false,
                null,
                $"No executor found for task type '{task.TaskType}'",
                new Dictionary<string, object?>(),
                TimeSpan.Zero);
        }

        try
        {
            _logger.LogDebug("Executing task {TaskId} (type: {TaskType})", task.Id, task.TaskType);
            return await executor.ExecuteAsync(task, caller, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Task {TaskId} cancelled", task.Id);
            var duration = DateTimeOffset.UtcNow - start;
            return new TaskResult(task.Id, false, null, "cancelled", new Dictionary<string, object?>(), duration);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Task {TaskId} failed", task.Id);
            var duration = DateTimeOffset.UtcNow - start;
            return new TaskResult(task.Id, false, null, ex.Message, new Dictionary<string, object?>(), duration);
        }
    }

    /// <summary>Validate the DAG: check for unknown dependencies and circular references.</summary>
    private void ValidateDag(IReadOnlyList<TaskDefinition> dag)
    {
        var ids = new HashSet<string>(dag.Select(t => t.Id));

        // Check for unknown dependencies
        foreach (var task in dag)
        {
            foreach (var dep in task.DependsOn)
            {
                if (!ids.Contains(dep))
                {
                    throw new ArgumentException(
                        $"Task '{task.Id}' depends on unknown task '{dep}'.");
                }
            }
        }

        // Cycle detection using DFS
        var visited = new HashSet<string>();
        var active = new HashSet<string>();
        var taskMap = dag.ToDictionary(t => t.Id);

        foreach (var task in dag)
        {
            if (!visited.Contains(task.Id))
            {
                DetectCycle(task.Id, taskMap, visited, active);
            }
        }
    }

    /// <summary>DFS-based cycle detection in the dependency graph.</summary>
    private static void DetectCycle(
        string id,
        Dictionary<string, TaskDefinition> map,
        HashSet<string> visited,
        HashSet<string> active)
    {
        active.Add(id);
        foreach (var dep in map[id].DependsOn)
        {
            if (active.Contains(dep))
            {
                throw new ArgumentException($"Circular dependency detected: {id} → {dep}");
            }

            if (!visited.Contains(dep))
            {
                DetectCycle(dep, map, visited, active);
            }
        }

        active.Remove(id);
        visited.Add(id);
    }
}
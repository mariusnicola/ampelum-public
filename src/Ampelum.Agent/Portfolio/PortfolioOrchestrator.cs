using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SignalGrid.Ampelum.Agent;

/// <summary>Fan-out orchestrator that runs high-level intents across all registered workspaces.
/// Uses SubagentOrchestrator per workspace and aggregates results.</summary>
public sealed class PortfolioOrchestrator : IPortfolioOrchestrator
{
    private readonly IProjectPortfolio _portfolio;
    private readonly IWorkspaceScheduler _scheduler;
    private readonly IPortfolioIntentDecomposer _decomposer;
    private readonly SubagentOrchestrator _subagentOrchestrator;
    private readonly ISuccessEvaluator? _successEvaluator;
    private readonly IToolDispatcher? _toolDispatcher;
    private readonly AgentIdentity? _callerIdentity;
    private readonly ILogger _logger;

    /// <param name="portfolio">The registered workspaces to fan out across.</param>
    /// <param name="scheduler">Workspace scheduler.</param>
    /// <param name="decomposer">Decomposes a high-level intent into per-workspace task DAGs.</param>
    /// <param name="successEvaluator">Optional judge for whether a workspace's task results actually satisfied the intent.</param>
    /// <param name="toolDispatcher">
    /// Real executor for task nodes. Required for any non-dry-run execution: without it,
    /// <see cref="RunAsync"/>/<see cref="StreamAsync"/> fail loud rather than reporting a fabricated success
    /// for work nothing actually performed. <paramref name="callerIdentity"/> must also be supplied whenever
    /// this is. Known follow-up gap: an observation token a DAG node's dispatch issues
    /// (<see cref="ToolResult.ObservationTokenId"/>) is not threaded into a dependent node's own dispatch
    /// call — <see cref="TaskExecutionResult"/> has no token field and nothing here feeds one node's issued
    /// token into another's args. A real DAG where a destructive-tier step depends on a token-issuing step
    /// will fail with a missing-token error; wiring that through is not yet built.
    /// </param>
    /// <param name="callerIdentity">The identity every dispatched task is attributed to. Required together with <paramref name="toolDispatcher"/>.</param>
    /// <param name="logger">Optional logger.</param>
    public PortfolioOrchestrator(
        IProjectPortfolio portfolio,
        IWorkspaceScheduler scheduler,
        IPortfolioIntentDecomposer decomposer,
        ISuccessEvaluator? successEvaluator = null,
        IToolDispatcher? toolDispatcher = null,
        AgentIdentity? callerIdentity = null,
        ILogger? logger = null)
    {
        if (toolDispatcher is not null && callerIdentity is null)
            throw new ArgumentException($"{nameof(callerIdentity)} must be supplied whenever {nameof(toolDispatcher)} is.", nameof(callerIdentity));
        _portfolio = portfolio;
        _scheduler = scheduler;
        _decomposer = decomposer;
        _successEvaluator = successEvaluator;
        _toolDispatcher = toolDispatcher;
        _callerIdentity = callerIdentity;
        _subagentOrchestrator = new SubagentOrchestrator(logger);
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<PortfolioResult> RunAsync(
        string intent,
        PortfolioOptions options,
        CancellationToken ct = default)
    {
        if (_portfolio.IsEmpty)
            return new PortfolioResult { OverallSuccess = true, CompletedAt = DateTimeOffset.UtcNow };

        var workspaces = ResolveWorkspaces(options.ProjectFilter);
        using var semaphore = new SemaphoreSlim(
            options.MaxConcurrentProjects > 0 ? options.MaxConcurrentProjects : workspaces.Count);
        var completed = new ConcurrentBag<WorkspaceExecutionResult>();
        var failures = new ConcurrentBag<(WorkspaceId, Exception)>();
        var escalations = new ConcurrentBag<PendingEscalation>();

        var tasks = workspaces.Select(async ws =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var result = await ExecuteWorkspaceAsync(ws, intent, options, ct);
                completed.Add(result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Workspace {WorkspaceId} failed", ws.Id);
                failures.Add((ws.Id, ex));
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        // OverallSuccess must reflect both workspace-level exceptions (`failures`) AND task-level failures
        // within an otherwise-completed workspace -- a caller that checks only this flag (never walking
        // CompletedWorkspaces itself) must not see `true` while every task in every workspace actually failed.
        var completedWorkspaces = completed.ToList();
        return new PortfolioResult
        {
            OverallSuccess = failures.IsEmpty && completedWorkspaces.All(ws => ws.Success),
            CompletedWorkspaces = completedWorkspaces,
            FailedWorkspaces = failures.ToList(),
            PendingEscalations = escalations.ToList(),
            CompletedAt = DateTimeOffset.UtcNow,
        };
    }

    public async IAsyncEnumerable<PortfolioProgressEvent> StreamAsync(
        string intent,
        PortfolioOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_portfolio.IsEmpty)
        {
            yield return new PortfolioProgressEvent
            {
                WorkspaceId = default,
                Message = "No workspaces registered.",
                Timestamp = DateTimeOffset.UtcNow,
            };
            yield break;
        }

        var workspaces = ResolveWorkspaces(options.ProjectFilter);
        using var semaphore = new SemaphoreSlim(
            options.MaxConcurrentProjects > 0 ? options.MaxConcurrentProjects : workspaces.Count);
        var active = new ConcurrentDictionary<WorkspaceId, bool>();

        foreach (var ws in workspaces)
        {
            active.TryAdd(ws.Id, true);
            yield return new PortfolioProgressEvent
            {
                WorkspaceId = ws.Id,
                Message = $"Starting workspace '{ws.Label}'",
                Timestamp = DateTimeOffset.UtcNow,
                Kind = PortfolioProgressKind.Info,
            };
        }

        var tasks = workspaces.Select(async ws =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var result = await ExecuteWorkspaceAsync(ws, intent, options, ct);
                active.TryRemove(ws.Id, out _);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Workspace {WorkspaceId} failed", ws.Id);
                active.TryRemove(ws.Id, out _);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    private IReadOnlyList<WorkspaceContext> ResolveWorkspaces(IReadOnlyList<WorkspaceId>? filter)
    {
        if (filter is null || filter.Count == 0)
            return _portfolio.GetAll();

        return filter
            .Select(id => _portfolio.Get(id))
            .Where(ws => ws is not null)
            .Select(ws => ws!)
            .ToList();
    }

    private async Task<WorkspaceExecutionResult> ExecuteWorkspaceAsync(
        WorkspaceContext ws,
        string intent,
        PortfolioOptions options,
        CancellationToken ct)
    {
        var summary = GatherProjectSummary(ws);

        var taskDefs = await _decomposer.DecomposeAsync(intent, summary, ws, ct);
        if (taskDefs.Length == 0)
        {
            return new WorkspaceExecutionResult
            {
                WorkspaceId = ws.Id,
                Success = true,
                TaskResults = Array.Empty<TaskExecutionResult>(),
            };
        }

        var dagNodes = BuildDag(taskDefs);
        var executor = MakeExecutor(ws, options);

        var taskResults = await _subagentOrchestrator.ExecuteAsync(dagNodes, executor, ct);

        SuccessJudgment? judgment = null;
        if (_successEvaluator is not null)
        {
            judgment = await _successEvaluator.EvaluatePortfolioAsync(ws, taskResults, ct);
        }

        return new WorkspaceExecutionResult
        {
            WorkspaceId = ws.Id,
            Success = taskResults.All(r => r.Success),
            TaskResults = taskResults,
            Judgment = judgment,
        };
    }

    private static ProjectSummary GatherProjectSummary(WorkspaceContext ws)
    {
        var summary = new ProjectSummary { WorkspaceId = ws.Id, RefreshedAt = DateTimeOffset.UtcNow };

        if (!string.IsNullOrEmpty(ws.RootPath) && Directory.Exists(ws.RootPath))
        {
            // Try roadmap
            var roadmapCandidates = new[]
            {
                Path.Combine(ws.RootPath, "docs", "roadmap", "index.md"),
                Path.Combine(ws.RootPath, "roadmap.md"),
            };

            foreach (var path in roadmapCandidates)
            {
                if (File.Exists(path))
                {
                    var lines = File.ReadAllLines(path);
                    var phaseLine = lines.FirstOrDefault(l =>
                        l.StartsWith("## Phase", StringComparison.OrdinalIgnoreCase));
                    summary = summary with { CurrentPhase = phaseLine?.Trim(' ', '#') ?? "Unknown" };
                    break;
                }
            }

            // Try backlog
            var backlogCandidates = new[]
            {
                Path.Combine(ws.RootPath, "docs", "backlog.md"),
                Path.Combine(ws.RootPath, "backlog.md"),
            };

            foreach (var path in backlogCandidates)
            {
                if (File.Exists(path))
                {
                    var items = File.ReadAllLines(path)
                        .Where(l => l.TrimStart().StartsWith("- [") && l.Contains(']'))
                        .Take(5)
                        .Select(l => l.Trim().TrimStart('-', ' ', '[', ']', 'x', ' '))
                        .ToList();
                    summary = summary with { TopBacklogItems = items };
                    break;
                }
            }

            // Try state store checkpoint
            if (!string.IsNullOrEmpty(summary.CurrentPhase))
            {
                summary = summary with
                {
                    LastExecutionOutcome = $"Current phase: {summary.CurrentPhase}"
                };
            }
        }

        return summary;
    }

    private static IReadOnlyList<TaskNode> BuildDag(TaskDefinition[] taskDefs)
    {
        return taskDefs.Select(td => new TaskNode
        {
            Id = td.Id,
            Description = td.Description,
            TaskType = td.TaskType,
            DependsOn = new HashSet<string>(td.DependsOn),
            Metadata = td.Metadata ?? new Dictionary<string, object?>(),
        }).ToList();
    }

    private Func<TaskNode, CancellationToken, Task<TaskExecutionResult>> MakeExecutor(
        WorkspaceContext ws, PortfolioOptions options)
    {
        return async (node, ct) =>
        {
            if (options.DryRun)
                return new TaskExecutionResult
                {
                    Success = true,
                    Output = $"[dry-run] Would execute '{node.Description}' in workspace '{ws.Label}'; no effect was actually attempted.",
                };

            if (_toolDispatcher is null)
                return new TaskExecutionResult
                {
                    Success = false,
                    Error = $"No IToolDispatcher configured; PortfolioOrchestrator refuses to fabricate a success result for '{node.Description}' in workspace '{ws.Label}'. Supply a toolDispatcher (and callerIdentity) or set PortfolioOptions.DryRun.",
                };

            var startedAt = DateTimeOffset.UtcNow;
            var args = node.Metadata.ToDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value), StringComparer.Ordinal);
            var context = new ToolInvocationContext(TraceId: ws.Id.ToString(), WorkflowId: node.Id, InvocationId: node.Id);
            var result = await _toolDispatcher.InvokeAsync(node.TaskType, args, _callerIdentity!, context, ct).ConfigureAwait(false);

            return new TaskExecutionResult
            {
                Success = result.Success,
                Output = result.Output,
                Error = result.Error,
                Duration = DateTimeOffset.UtcNow - startedAt,
            };
        };
    }
}
using System.Text.Json;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Agent.Contracts;
using SignalGrid.Ampelum.Agent.Orchestration;

namespace Ampelum.Samples.AgentRuntime;

/// <summary>
/// One incarnation of the triage program. It owns every store it needs, so a second instance on the same state folder
/// behaves like the program started again after a crash: it sees the checkpoints and the event log the first one wrote.
/// </summary>
public sealed class TriageProcess : IAsyncDisposable
{
    const string FirstPhase = "assess";
    const string ResumePhase = "report";
    const string PlanFile = "backlog-triage.yml";

    readonly string _stateDirectory;
    readonly IChatClient _chat;
    readonly JsonFileProjectRegistry _registry;
    readonly JsonFileDurableRunEventStore _events;

    /// <summary>Creates the process. A null <paramref name="policy"/> is read from the project folder.</summary>
    public TriageProcess(string root, string stateDirectory, IChatClient chat, ModelTiers tiers, TriagePolicy? policy = null)
    {
        Root = root;
        _stateDirectory = stateDirectory;
        _chat = chat;
        Policy = policy ?? TriagePolicy.Load(root);
        Router = tiers.CreateRouter(Policy);
        Budget = new ModelBudget(Policy.ToBudget());

        Skills = new TriageSkills();
        _registry = new JsonFileProjectRegistry(Path.Combine(stateDirectory, "portfolio.json"));
        Workspace = TriageWorkspace.Open(_registry, root, Skills);
        Readable = TriageWorkspace.ReadableScope(Workspace, Policy);
        Facts = new CodeFacts(root, Policy.ReadablePaths.Select(p => Path.Combine(root, p)), Readable);

        Store = new SqliteWorkflowPersistence(Path.Combine(stateDirectory, "workflows.db"));
        _events = new JsonFileDurableRunEventStore(Path.Combine(stateDirectory, "events"));
        Runtime = new DurableAgentRuntime(new JsonFileStateStore(Path.Combine(stateDirectory, "checkpoints")), _events);
        Notes = new PhaseNotes(new JsonFileStateStore(Path.Combine(stateDirectory, "notes")));
        Caller = new AgentIdentity("triage-agent", Guid.NewGuid().ToString("N"), ["triage"], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
    }

    public string Root { get; }

    public TriagePolicy Policy { get; }

    public IModelRouter Router { get; }

    public ModelBudget Budget { get; }

    public TriageSkills Skills { get; }

    public WorkspaceContext Workspace { get; }

    public BlastRadiusScope Readable { get; }

    public CodeFacts Facts { get; }

    public SqliteWorkflowPersistence Store { get; }

    public DurableAgentRuntime Runtime { get; }

    public IDurableRunEventStore RunEvents => _events;

    public PhaseNotes Notes { get; }

    public AgentIdentity Caller { get; }

    public RunJournal Journal { get; } = new();

    public AuditResults Results { get; } = new();

    public SessionGrantStore Grants { get; } = new();

    internal IProjectPortfolio Registry => _registry;

    /// <summary>Editor notes of the refine loop. Process-local between iterations; the loop's final checkpoint keeps them.</summary>
    internal List<string> EditorNotes { get; } = [];

    /// <summary>Client for a kind of work: the router picks the model id, the budget guards the call.</summary>
    public IChatClient ClientFor(string taskType) =>
        new BudgetedChatClient(Pinned(taskType), Budget);

    /// <summary>The model id the router picks for a kind of work.</summary>
    public ModelRoute RouteFor(string taskType) => Router.Route(new TaskSignal { TaskType = taskType });

    IChatClient Pinned(string taskType) => new PinnedModelClient(_chat, RouteFor(taskType).ModelId);

    /// <summary>Asks the planner for the workflow and stores it under the state folder.</summary>
    public Task<PlannedWorkflow> PlanAsync(string goal, CancellationToken ct = default) =>
        CreatePlanner().PlanWorkflowAsync(goal, TriageTools.Surveys, Path.Combine(_stateDirectory, "workflows", PlanFile), ct);

    internal TriagePlanner CreatePlanner() => new(Pinned("plan"));

    /// <summary>Starts the durable run and its notes.</summary>
    public async Task<DurableRunId> StartRunAsync(string goal, CancellationToken ct = default)
    {
        var runId = await Runtime.StartAsync(
            new DurableIntent(Workspace.Id.Value, DurableWorkflowKind.PortfolioTask, goal),
            DurableRunOptions.Default(Workspace.Id.Value),
            ct).ConfigureAwait(false);
        await Notes.OpenAsync(runId.Value, goal, FirstPhase, ct).ConfigureAwait(false);
        return runId;
    }

    /// <summary>
    /// Runs the workflow until it reaches the approval step, where the run is paused and the first process stops
    /// making progress, as when it is killed while a person has not answered yet.
    /// </summary>
    public async Task<InterruptedTriage> RunUntilApprovalAsync(DurableRunId runId, string executionId, WorkflowDefinition definition, CancellationToken ct = default)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abandon = new CancellationTokenSource();
        var steps = new TriageSteps(this, runId, executionId, definition, async stop =>
        {
            reached.TrySetResult();
            // The runtime has no way to report "waiting for a person", so a handler can only block or fail.
            await Task.Delay(Timeout.Infinite, abandon.Token).ConfigureAwait(false);
        });

        var request = new WorkflowExecutionRequest(executionId, definition, nameof(TriageProcess), DateTimeOffset.UtcNow);
        var run = new DurableWorkflowRuntime(Store, Store).RunAsync(request, steps.HandleAsync, ct);
        if (await Task.WhenAny(reached.Task, run).ConfigureAwait(false) == run)
        {
            abandon.Dispose();
            var snapshot = await run.ConfigureAwait(false);
            throw new InvalidOperationException($"Execution '{executionId}' ended as {snapshot.Status} before it reached the approval step: {snapshot.FailureReason}");
        }
        return new InterruptedTriage(run, abandon);
    }

    /// <summary>The person's decision: resumes the paused run with the token it issued.</summary>
    public async Task ApproveAsync(DurableRunId runId, string reason, CancellationToken ct = default)
    {
        var snapshot = await Runtime.GetSnapshotAsync(runId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Run '{runId}' is unknown.");
        var pending = snapshot.PendingInterrupt
            ?? throw new InvalidOperationException($"Run '{runId}' is not waiting for anyone.");
        await Runtime.ResumeAsync(runId, new DurableResumeInput(pending.ResumeToken, reason, ResumePhase), ct).ConfigureAwait(false);
    }

    /// <summary>Resumes the stored workflow execution and returns what the finished run left in the stores.</summary>
    public async Task<TriageOutcome> FinishAsync(DurableRunId runId, string executionId, CancellationToken ct = default)
    {
        var snapshot = await Runtime.GetSnapshotAsync(runId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Run '{runId}' is unknown.");
        if (snapshot.PendingInterrupt is not null)
            throw new InvalidOperationException($"Run '{runId}' still waits for approval.");

        var stored = await Store.LoadAsync(executionId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No stored execution '{executionId}'.");
        var steps = new TriageSteps(this, runId, executionId, stored.Request.Workflow, waitForPerson: null);
        var finished = await new DurableWorkflowRuntime(Store, Store).ResumeAsync(stored.Request, steps.HandleAsync, ct).ConfigureAwait(false);
        return await ReadOutcomeAsync(runId, finished, ct).ConfigureAwait(false);
    }

    async Task<TriageOutcome> ReadOutcomeAsync(DurableRunId runId, DurableWorkflowSnapshot finished, CancellationToken ct)
    {
        var workflow = finished.Request.Workflow;
        var assessed = TriageSteps.ReadDetail<AssessDetail>(finished, workflow, WorkflowStepKind.FanOut) ?? new AssessDetail([], [], 0);
        var notes = TriageSteps.ReadDetail<List<string>>(finished, workflow, WorkflowStepKind.Loop) ?? [];
        var reportStep = workflow.Steps.First(s => s.Kind == WorkflowStepKind.Tool);
        var reportPath = finished.Nodes[reportStep.StepId].Status == DurableWorkflowNodeStatus.Succeeded
            ? Path.Combine(Root, ReportWriter.FileName)
            : null;

        var now = DateTimeOffset.UtcNow;
        var outcomes = assessed.Results
            .Select(_ => new OutcomeRecord(finished.Request.ExecutionId, workflow.WorkflowId, WorkflowExecutionStatus.Succeeded, now))
            .Concat(assessed.Deferred.Select(_ => new OutcomeRecord(finished.Request.ExecutionId, workflow.WorkflowId, WorkflowExecutionStatus.Failed, now, RiskEvents: ["model budget"])))
            .ToArray();

        return new TriageOutcome(
            finished.Status,
            finished.FailureReason,
            assessed,
            notes,
            reportPath,
            await Store.QueryAuditAsync(finished.Request.ExecutionId, ct).ConfigureAwait(false),
            await _events.ReadAsync(runId, ct).ConfigureAwait(false),
            await new DefaultWorkflowMonitor().EvaluateAsync(workflow.WorkflowId, outcomes, ct).ConfigureAwait(false));
    }

    public async ValueTask DisposeAsync()
    {
        Facts.Dispose();
        await Journal.DisposeAsync().ConfigureAwait(false);
        await Workspace.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Handle to an execution stopped at the approval step. Disposing it releases the abandoned first runtime.</summary>
public sealed class InterruptedTriage : IAsyncDisposable
{
    readonly Task<DurableWorkflowSnapshot> _run;
    readonly CancellationTokenSource _abandon;

    internal InterruptedTriage(Task<DurableWorkflowSnapshot> run, CancellationTokenSource abandon)
    {
        _run = run;
        _abandon = abandon;
    }

    /// <summary>Lets the abandoned runtime finish; it loses its checkpoint race against the resumed one and writes nothing.</summary>
    public async ValueTask DisposeAsync()
    {
        await _abandon.CancelAsync().ConfigureAwait(false);
        try
        {
            await _run.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _abandon.Dispose();
    }
}

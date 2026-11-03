using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Agent.Contracts;
using SignalGrid.Ampelum.Agent.Orchestration;

namespace Ampelum.Samples.AgentRuntime;

/// <summary>
/// The handler the durable runtime calls for every step. The runtime only knows step kinds; what a fan-out, a loop,
/// a join or a tool call does is decided here. The runtime itself parks Approval steps until a decision arrives.
/// </summary>
internal sealed class TriageSteps
{
    readonly TriageProcess _process;
    readonly DurableRunId _runId;
    readonly string _executionId;
    readonly WorkflowDefinition _definition;
    readonly ToolDispatcher _dispatcher;
    readonly RunSupervisor _supervisor;
    int _completed;

    public TriageSteps(TriageProcess process, DurableRunId runId, string executionId, WorkflowDefinition definition)
    {
        _process = process;
        _runId = runId;
        _executionId = executionId;
        _definition = definition;
        _supervisor = new RunSupervisor(runId.Value, definition.Steps[0].StepId);

        var backlog = DemoProject.ReadBacklog(process.Root);
        var auditor = new IssueAuditor(process.Root, executionId, process.Policy, process.Readable, process.Facts, process.Skills,
            process.ClientFor, process.Journal, process.Budget, process.Results);
        _dispatcher = TriageTools.CreateDispatcher(process.Caller, process.Grants, backlog, auditor.AuditAsync, new ReportWriter(process.Root, process.Workspace.MaxBlastRadius));
    }

    public async Task<DurableWorkflowNodeResult> HandleAsync(DurableWorkflowNodeContext context, CancellationToken ct)
    {
        var step = context.Step;
        if (_supervisor.Phase != step.StepId)
            _supervisor.Enter(step.StepId);

        var result = step.Kind switch
        {
            WorkflowStepKind.FanOut => await AssessAsync(ct).ConfigureAwait(false),
            WorkflowStepKind.Join => await CollectAsync(ct).ConfigureAwait(false),
            WorkflowStepKind.Loop => await RefineAsync(ct).ConfigureAwait(false),
            // The runtime parks Approval steps itself and never hands them to this handler, so there is no case for it.
            // The runtime runs Wait like any other step and does not pause for it, so nothing is awaited here.
            WorkflowStepKind.Wait => DurableWorkflowNodeResult.Success("no external signal is awaited"),
            WorkflowStepKind.Tool => await ReportAsync(context, ct).ConfigureAwait(false),
            _ => DurableWorkflowNodeResult.Failure($"Step '{step.StepId}' has kind '{step.Kind}', which this sample does not handle."),
        };

        if (result.Succeeded && !result.LoopContinue)
            _supervisor.Count(_definition.Steps.Count, ++_completed, 0);
        if (!result.Succeeded)
            _supervisor.Finish(succeeded: false);
        return result;
    }

    async Task<DurableWorkflowNodeResult> AssessAsync(CancellationToken ct)
    {
        var snapshot = await _process.Runtime.GetSnapshotAsync(_runId, ct).ConfigureAwait(false);
        var goal = snapshot!.Goal;
        await _process.Journal.RecordIntentAsync(goal, ct).ConfigureAwait(false);

        var portfolio = new PortfolioOrchestrator(
            _process.Registry, new RoundRobinScheduler(), _process.CreatePlanner().Decomposer,
            new DefaultSuccessEvaluator(), _dispatcher, _process.Caller);
        var run = await portfolio.RunAsync(goal, new PortfolioOptions { MaxConcurrentProjects = 1, ProjectFilter = [_process.Workspace.Id] }, ct).ConfigureAwait(false);
        if (run.FailedWorkspaces.Count > 0)
            return DurableWorkflowNodeResult.Failure(string.Join("; ", run.FailedWorkspaces.Select(f => f.Exception.Message)));

        var workspace = run.CompletedWorkspaces.Single();
        var results = _process.Results;
        var failed = workspace.TaskResults.Where(r => !r.Success).ToArray();
        // A task that failed for a reason other than the budget has no entry in the deferred list and fails the step.
        if (failed.Length != results.Deferred.Count)
            return DurableWorkflowNodeResult.Failure(string.Join("; ", failed.Select(f => f.Error)));

        var detail = new AssessDetail(results.Results, results.Deferred, workspace.Judgment?.Score ?? 0);
        return DurableWorkflowNodeResult.Success(JsonSerializer.Serialize(detail));
    }

    async Task<DurableWorkflowNodeResult> CollectAsync(CancellationToken ct)
    {
        await _process.Notes.ArchiveAsync(_runId.Value, "refine", ct).ConfigureAwait(false);
        return DurableWorkflowNodeResult.Success("assessments collected");
    }

    // One pass of the editor loop. The runtime repeats the step while the verdict is "revise", up to the step's iteration bound.
    async Task<DurableWorkflowNodeResult> RefineAsync(CancellationToken ct)
    {
        var assessed = await ReadStoredAsync<AssessDetail>(WorkflowStepKind.FanOut, ct).ConfigureAwait(false) ?? new AssessDetail([], [], 0);
        var notes = await _process.Notes.ContextAsync(_runId.Value, ct).ConfigureAwait(false);
        var journal = await _process.Journal.RenderAsync(ct).ConfigureAwait(false);
        var context = string.Join('\n', notes.Select(n => n.Content).Concat(journal.Select(m => m.Content)));

        var again = false;
        try
        {
            var predict = new Predict<ReportRefinement>(new AmpelumContext(_process.ClientFor("refine"), new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance));
            var answer = await predict.ForwardAsync(new ReportRefinement { Draft = ReportComposer.Draft(assessed), Context = context }, ct: ct).ConfigureAwait(false);
            await _process.Journal.RecordUsageAsync(answer.Usage(), ct).ConfigureAwait(false);
            _process.EditorNotes.Add(answer.Value.Note);
            again = string.Equals(answer.Value.Verdict.Trim(), "revise", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (BudgetExhaustedException.Find(ex) is { } budget)
        {
            _process.EditorNotes.Add($"Editor stopped: {budget.Message}");
        }
        return new DurableWorkflowNodeResult(true, Detail: JsonSerializer.Serialize(_process.EditorNotes), LoopContinue: again);
    }

    /// <summary>Records in the in-memory state machine that the run now waits for a person.</summary>
    public void WaitForPerson(string reason) => _supervisor.WaitForPerson(TriageTools.PostReport, reason);

    // The grant for the report tool exists only when the stored decision of the approval step is an approval.
    // Without one the tool stays refused, so a run that was never approved cannot post.
    async Task<DurableWorkflowNodeResult> ReportAsync(DurableWorkflowNodeContext context, CancellationToken ct)
    {
        var approvalStep = _definition.Steps.FirstOrDefault(s => s.Kind == WorkflowStepKind.Approval);
        var stored = await _process.Store.LoadAsync(_executionId, ct).ConfigureAwait(false);
        var decision = approvalStep is null ? null : stored?.Nodes[approvalStep.StepId].Decision;
        if (decision is { Approved: true })
        {
            _process.Grants.Add(new SessionGrant(
                Guid.NewGuid().ToString("N"), _process.Caller.InstanceId, TriageTools.PostReport, PolicyDecision.Allowed,
                DateTimeOffset.UtcNow.AddMinutes(_process.Policy.GrantMinutes), decision.Reason ?? $"approved by {decision.DecidedBy}"));
        }

        var assessed = await ReadStoredAsync<AssessDetail>(WorkflowStepKind.FanOut, ct).ConfigureAwait(false) ?? new AssessDetail([], [], 0);
        var editorNotes = await ReadStoredAsync<List<string>>(WorkflowStepKind.Loop, ct).ConfigureAwait(false) ?? [];
        var notes = await _process.Notes.ContextAsync(_runId.Value, ct).ConfigureAwait(false);
        var arguments = new Dictionary<string, JsonElement>
        {
            [TriageTools.ContentArgument] = JsonSerializer.SerializeToElement(ReportComposer.Compose(notes, assessed, editorNotes)),
        };

        var handler = new WorkflowToolStepHandler(_dispatcher, _process.Caller, new WorkflowPolicyGate(), _ => arguments);
        return await handler.HandleAsync(context, ct).ConfigureAwait(false);
    }

    async Task<T?> ReadStoredAsync<T>(WorkflowStepKind kind, CancellationToken ct)
    {
        var stored = await _process.Store.LoadAsync(_executionId, ct).ConfigureAwait(false);
        return stored is null ? default : ReadDetail<T>(stored, _definition, kind);
    }

    /// <summary>Reads the JSON a step stored in its checkpoint. The first step of that kind is used.</summary>
    internal static T? ReadDetail<T>(DurableWorkflowSnapshot snapshot, WorkflowDefinition definition, WorkflowStepKind kind)
    {
        var step = definition.Steps.FirstOrDefault(s => s.Kind == kind);
        var detail = step is null ? null : snapshot.Nodes[step.StepId].Detail;
        return detail is null ? default : JsonSerializer.Deserialize<T>(detail);
    }
}

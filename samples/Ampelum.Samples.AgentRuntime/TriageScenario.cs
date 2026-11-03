using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Agent.Contracts;

namespace Ampelum.Samples.AgentRuntime;

/// <summary>What the scenario saw on its way: the plan, the paused run seen by the restarted process, and the final outcome.</summary>
public sealed record ScenarioResult(
    PlannedWorkflow Plan,
    DurableRunId RunId,
    string ExecutionId,
    DurableRunSnapshot PausedSnapshot,
    TriageOutcome Outcome,
    FixOutcome? Fix = null,
    DurableRunSnapshot? InProcessResume = null);

/// <summary>
/// The whole story in one place. A first runtime plans the run, assesses the backlog and returns when the workflow runtime
/// parks the approval step. A second runtime with new objects on the same state folder reads the event log, delivers the
/// approval to the waiting step and finishes the workflow.
/// Both run inside one operating system process.
/// </summary>
public static class TriageScenario
{
    /// <summary>The goal every phase of the run is reminded of.</summary>
    public const string Goal = "Triage the open backlog issues of the checkout project and write a report.";

    public static async Task<ScenarioResult> RunAsync(
        string workDirectory,
        IChatClient chat,
        ModelTiers tiers,
        TextWriter? log = null,
        TriagePolicy? policy = null,
        CancellationToken ct = default,
        IChatClient? fixer = null)
    {
        var root = DemoProject.Extract(Path.Combine(workDirectory, "project"));
        var state = Path.Combine(workDirectory, "state");
        Directory.CreateDirectory(state);
        var executionId = Guid.NewGuid().ToString("N");

        PlannedWorkflow plan;
        DurableRunId runId;
        DurableRunSnapshot inProcess;
        await using (var first = new TriageProcess(root, state, chat, tiers, policy))
        {
            inProcess = await first.PauseAndResumeInProcessAsync(Goal, ct).ConfigureAwait(false);
            log?.WriteLine($"Pause and resume on one runtime instance: a separate run record ends {inProcess.State}, phase '{inProcess.CurrentPhase}'.");

            plan =await first.PlanAsync(Goal, ct).ConfigureAwait(false);
            log?.WriteLine($"Plan: {plan.Definition.Name} v{plan.Version}, steps: {string.Join(" > ", plan.Definition.Steps.Select(s => $"{s.StepId}[{s.Kind}]"))}");

            runId = await first.StartRunAsync(Goal, ct).ConfigureAwait(false);
            var stopped = await first.RunUntilApprovalAsync(runId, executionId, plan.Definition, ct).ConfigureAwait(false);
            var approvalStep = plan.Definition.Steps.First(s => s.Kind == WorkflowStepKind.Approval).StepId;
            log?.WriteLine($"First runtime returned with status {stopped.Status}: workflow step '{approvalStep}' is {stopped.Nodes[approvalStep].Status}. Model calls so far: {first.Budget.Calls}, tokens: {first.Budget.Tokens}.");
            log?.WriteLine("Both runtimes live in this one OS process; they share no memory, only the state folder.");

            await using var second = new TriageProcess(root, state, chat, tiers, policy);
            var paused = await second.Runtime.GetSnapshotAsync(runId, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("The run is gone.");
            log?.WriteLine($"Second runtime reads the run record from the state folder: state {paused.State}, run phase '{paused.CurrentPhase}' (the run record's phase, not a workflow step), pending: {paused.PendingInterrupt?.Prompt}");

            await second.ApproveAsync(runId, executionId, "Maintainer approved the report", ct).ConfigureAwait(false);
            var outcome = await second.FinishAsync(runId, executionId, ct).ConfigureAwait(false);
            Print(log, outcome);

            FixOutcome? fix = null;
            if (fixer is not null && outcome.ReportPath is not null)
            {
                fix = await IssueFixer.FixAsync(root, IssueFixer.WithToolInvocation(fixer), ct: ct).ConfigureAwait(false);
                log?.WriteLine($"Sub-agent fix of #102: {(fix.Passed ? "[OK]" : "[FAILED]")} {fix.Detail}. Sub-agent summary: {fix.Run.Outcome}");
            }
            return new ScenarioResult(plan, runId, executionId, paused, outcome, fix, inProcess);
        }
    }

    static void Print(TextWriter? log, TriageOutcome outcome)
    {
        if (log is null)
            return;
        log.WriteLine($"Run events: {string.Join(", ", outcome.RunEvents.Select(e => e.Kind))}");
        foreach (var result in outcome.Assessed.Results)
            log.WriteLine($"  #{result.Issue} {result.Severity,-6} skill={result.Skill} ({result.SkillRoute}) {result.Cause}");
        foreach (var deferred in outcome.Assessed.Deferred)
            log.WriteLine($"  #{deferred.Issue} deferred: {deferred.Reason}");
        log.WriteLine($"Editor notes: {string.Join(" ", outcome.EditorNotes)}");
        log.WriteLine($"Health: {outcome.Health.SuccessRate:P0} success over {outcome.Health.SampleSize} issues. {string.Join(" ", outcome.Health.Concerns ?? [])}");
        log.WriteLine($"[{outcome.Status}] report (file removed after this run):");
        if (outcome.ReportPath is not null)
            log.WriteLine(File.ReadAllText(outcome.ReportPath));
    }
}

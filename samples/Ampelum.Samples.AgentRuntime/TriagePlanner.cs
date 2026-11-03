using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Agent.Contracts;
using SignalGrid.Ampelum.Agent.Synthesis;
using SignalGrid.Ampelum.Agent.Yaml;

namespace Ampelum.Samples.AgentRuntime;

/// <summary>The workflow the planner wrote, where it was stored and how many model attempts it took.</summary>
public sealed record PlannedWorkflow(WorkflowDefinition Definition, string Path, string Version);

/// <summary>
/// Plans the run: the model proposes the workflow as YAML and the backlog tasks, and nothing is trusted until it
/// has been compiled or parsed by the library. A planned workflow only goes live through <c>PromoteAsync</c>.
/// </summary>
public sealed class TriagePlanner(IChatClient planningModel)
{
    readonly ChatSynthesisInfrastructure _infrastructure = new(planningModel);

    /// <summary>Decomposes an intent into backlog tasks for one workspace; used by the portfolio orchestrator.</summary>
    public IPortfolioIntentDecomposer Decomposer => new PortfolioIntentDecomposer(_infrastructure);

    /// <summary>Asks for a workflow, promotes it to <paramref name="path"/> and compiles what was written.</summary>
    public async Task<PlannedWorkflow> PlanWorkflowAsync(string task, IReadOnlyList<ToolSurvey> tools, string path, CancellationToken ct = default)
    {
        var synthesizer = new DefaultWorkflowSynthesizer(_infrastructure);
        var created = await synthesizer.CreateAsync(task, tools, path, ct).ConfigureAwait(false);
        if (!created.Success || created.Candidate is null)
            throw new InvalidOperationException("The planner did not produce a valid workflow: " + string.Join("; ", created.ValidationErrors));

        var promoted = await synthesizer.PromoteAsync(created.Candidate, reviewApproved: false, ct).ConfigureAwait(false);
        if (!promoted.Success || promoted.WorkflowPath is null)
            throw new InvalidOperationException("The workflow was not promoted: " + string.Join("; ", promoted.Errors));

        return new PlannedWorkflow(Compile(promoted.WorkflowPath), promoted.WorkflowPath, promoted.Version!);
    }

    /// <summary>Compiles a stored workflow file.</summary>
    public static WorkflowDefinition Compile(string path)
    {
        var compiled = WorkflowYamlCompiler.Compile(File.ReadAllText(path));
        return compiled.Success && compiled.Artifact is not null
            ? compiled.Artifact.Definition
            : throw new InvalidOperationException("Workflow YAML did not compile: " + string.Join("; ", compiled.Errors));
    }
}

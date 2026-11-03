using System.Runtime.CompilerServices;
using Ampelum.Samples.AgentRuntime;
using Microsoft.Data.Sqlite;
using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Agent.Contracts;
using Xunit;

namespace Ampelum.Samples.AgentRuntime.Tests;

static class TestEnvironment
{
    // The convention enforcer lets this variable replace the project's manifest. An empty value is ignored by the
    // library, so the tests read the project's own manifest whatever the machine has set.
    [ModuleInitializer]
    internal static void NeutraliseConventionOverride() =>
        Environment.SetEnvironmentVariable("AMPELUM_CONVENTIONS", string.Empty);
}

/// <summary>A temp folder with the demo project and an empty state folder, removed on dispose.</summary>
sealed class TempWorkspace : IDisposable
{
    public TempWorkspace()
    {
        Directory = Path.Combine(Path.GetTempPath(), "ampelum-agent-runtime-tests", Guid.NewGuid().ToString("N"));
        Root = DemoProject.Extract(Path.Combine(Directory, "project"));
        State = Path.Combine(Directory, "state");
        System.IO.Directory.CreateDirectory(State);
    }

    public string Directory { get; }

    public string Root { get; }

    public string State { get; }

    readonly List<TriageProcess> _processes = [];

    public TriageProcess NewProcess(FakeTriageModel model, TriagePolicy? policy = null)
    {
        var process = new TriageProcess(Root, State, model, FakeTriageModel.Tiers, policy);
        _processes.Add(process);
        return process;
    }

    // A test that fails halfway leaves its processes open and the code index locked, so they are closed here
    // before the folder goes.
    public void Dispose()
    {
        foreach (var process in _processes)
        {
            try
            {
                process.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (ObjectDisposedException)
            {
            }
        }
        SqliteConnection.ClearAllPools();
        System.IO.Directory.Delete(Directory, recursive: true);
    }
}

/// <summary>A run parked at the approval step, with the process that ran it still open for inspection.</summary>
sealed class PausedRun : IAsyncDisposable
{
    public const string ExecutionId = "paused-run";

    readonly TempWorkspace _workspace;
    readonly FakeTriageModel _model;
    readonly TriagePolicy? _policy;

    PausedRun(TempWorkspace workspace, FakeTriageModel model, TriagePolicy? policy, TriageProcess first, PlannedWorkflow plan, DurableRunId runId)
    {
        _workspace = workspace;
        _model = model;
        _policy = policy;
        First = first;
        Plan = plan;
        RunId = runId;
    }

    public TriageProcess First { get; }

    public PlannedWorkflow Plan { get; }

    public DurableRunId RunId { get; }

    public static async Task<PausedRun> StartAsync(
        TempWorkspace workspace,
        FakeTriageModel model,
        TriagePolicy? policy = null,
        Func<WorkflowDefinition, WorkflowDefinition>? adjust = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var first = workspace.NewProcess(model, policy);
        var plan = await first.PlanAsync(TriageScenario.Goal, ct);
        var runId = await first.StartRunAsync(TriageScenario.Goal, ct);
        await first.RunUntilApprovalAsync(runId, ExecutionId, adjust?.Invoke(plan.Definition) ?? plan.Definition, ct);
        return new PausedRun(workspace, model, policy, first, plan, runId);
    }

    /// <summary>Starts a second process on the same state, records the person's approval and finishes the run.</summary>
    public async Task<TriageOutcome> ApproveAndFinishAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var second = _workspace.NewProcess(_model, _policy);
        await second.ApproveAsync(RunId, ExecutionId, "Maintainer approved", ct);
        return await second.FinishAsync(RunId, ExecutionId, ct);
    }

    public async ValueTask DisposeAsync() => await First.DisposeAsync();
}

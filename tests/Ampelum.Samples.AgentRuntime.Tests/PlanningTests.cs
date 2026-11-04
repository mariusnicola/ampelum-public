using Ampelum.Samples.AgentRuntime;
using Shouldly;
using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Agent.Contracts;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace Ampelum.Samples.AgentRuntime.Tests;

public sealed class PlanningTests : IDisposable
{
    const string Broken = "id: broken\nversion: 1.0.0\nsteps:\n  - id: only\n    kind: teleport\n";

    readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    string PlanPath => Path.Combine(_workspace.State, "workflows", "plan.yml");

    [Fact]
    public async Task Planner_WritesTheWorkflowOnlyAfterTheLibraryAcceptedIt()
    {
        var planner = new TriagePlanner(new FakeTriageModel());

        var plan = await planner.PlanWorkflowAsync(TriageScenario.Goal, TriageTools.Surveys, PlanPath, TestContext.Current.CancellationToken);

        plan.Path.ShouldBe(PlanPath);
        plan.Version.ShouldBe("1.0.0");
        File.ReadAllText(PlanPath).ShouldBe(Embedded.ReadText("fake/plan.yml").TrimEnd('\n', '\r'), StringCompareShould.IgnoreLineEndings);
        plan.Definition.Steps.Select(s => s.Kind).ShouldBe(
        [
            WorkflowStepKind.FanOut, WorkflowStepKind.Join, WorkflowStepKind.Loop,
            WorkflowStepKind.Approval, WorkflowStepKind.Wait, WorkflowStepKind.Tool,
        ]);
        plan.Definition.Steps.Single(s => s.Kind == WorkflowStepKind.Approval).MaxAutonomy.ShouldBe(AutonomyMode.RequireApproval);
        plan.Definition.Policy.ShouldNotBeNull().MaxEffectClass.ShouldBe(EffectClass.Additive);
    }

    [Fact]
    public async Task Planner_AsksAgainWithTheCompilerErrors_WhenTheFirstAnswerDoesNotCompile()
    {
        var model = new FakeChatClient().EnqueueText(Broken).EnqueueText(Embedded.ReadText("fake/plan.yml"));

        var plan = await new TriagePlanner(model).PlanWorkflowAsync(TriageScenario.Goal, TriageTools.Surveys, PlanPath, TestContext.Current.CancellationToken);

        plan.Definition.WorkflowId.ShouldBe("backlog-triage");
        model.RecordedCalls.Count.ShouldBe(2);
        model.RecordedCalls[1].Messages.Single().Text.ShouldContain("The previous answer was rejected");
        model.RecordedCalls[1].Messages.Single().Text.ShouldContain("teleport");
    }

    [Fact]
    public async Task Planner_GivesUpAfterThreeRejectedAnswers_AndWritesNothing()
    {
        var model = new FakeChatClient().EnqueueText(Broken).EnqueueText(Broken).EnqueueText(Broken);

        var failure = await Should.ThrowAsync<InvalidOperationException>(() =>
            new TriagePlanner(model).PlanWorkflowAsync(TriageScenario.Goal, TriageTools.Surveys, PlanPath, TestContext.Current.CancellationToken));

        failure.Message.ShouldContain("did not produce a valid workflow");
        model.RecordedCalls.Count.ShouldBe(3);
        File.Exists(PlanPath).ShouldBeFalse();
    }

    [Fact]
    public async Task Decomposer_TurnsTheBacklogIntoOneAuditTaskPerIssue()
    {
        await using var process = _workspace.NewProcess(new FakeTriageModel());
        var summary = new ProjectSummary { WorkspaceId = process.Workspace.Id, TopBacklogItems = DemoProject.ReadBacklog(_workspace.Root).Select(i => i.Headline).ToArray() };

        var tasks = await process.CreatePlanner().Decomposer.DecomposeAsync(TriageScenario.Goal, summary, process.Workspace, TestContext.Current.CancellationToken);

        tasks.Select(t => (t.Id, t.TaskType)).ShouldBe([("audit-101", "audit"), ("audit-102", "audit"), ("audit-103", "audit")]);
        tasks.ShouldAllBe(t => t.DependsOn.Count == 0);
    }

    [Fact]
    public async Task Decomposer_ReturnsNoTasks_WhenTheModelNeverAnswersWithATaskList()
    {
        await using var process = _workspace.NewProcess(new FakeTriageModel());
        var model = new FakeChatClient().EnqueueText("no list").EnqueueText("still none").EnqueueText("nothing");
        var summary = new ProjectSummary { WorkspaceId = process.Workspace.Id };

        var tasks = await new TriagePlanner(model).Decomposer.DecomposeAsync(TriageScenario.Goal, summary, process.Workspace, TestContext.Current.CancellationToken);

        tasks.ShouldBeEmpty();
    }

    [Fact]
    public async Task CompiledBriefs_AreAuditedByHashAndNeverCarryTheirInputs()
    {
        await using var paused = await PausedRun.StartAsync(_workspace, new FakeTriageModel());

        var results = paused.First.Results.Results;
        var audit = paused.First.Results.PromptAudit;

        results.Select(r => r.BriefHash).Distinct().Count().ShouldBe(3);
        audit.Select(a => a.Metadata!["prompt.artifact.hash"]).ShouldBe(results.Select(r => r.BriefHash), ignoreOrder: true);
        audit.ShouldAllBe(a => a.EventType == "prompt.compiled");
        audit.ShouldAllBe(a => a.CorrelationId == PausedRun.ExecutionId && a.ActorId == "triage-auditor");
        audit.ShouldAllBe(a => a.ExecutionId == PausedRun.ExecutionId);
        audit.SelectMany(a => a.Metadata!.Values).ShouldNotContain(value => value.Contains("empty cart", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SameIssueInTwoSeparateRuns_CompilesToTheSameBriefHash()
    {
        var firstRun = await TriageScenario.RunAsync(_workspace.Directory, new FakeTriageModel(), FakeTriageModel.Tiers, ct: TestContext.Current.CancellationToken);
        using var other = new TempWorkspace();
        var secondRun = await TriageScenario.RunAsync(other.Directory, new FakeTriageModel(), FakeTriageModel.Tiers, ct: TestContext.Current.CancellationToken);

        var first = firstRun.Outcome.Assessed.Results.Select(r => (r.Issue, r.BriefHash)).Order().ToArray();
        var second = secondRun.Outcome.Assessed.Results.Select(r => (r.Issue, r.BriefHash)).Order().ToArray();

        first.Length.ShouldBe(3);
        second.ShouldBe(first);
    }

    [Fact]
    public async Task ContextManifest_UsesOnlyEvidenceTheCallerMayRead_AndNamesWhatItLeftOut()
    {
        await using var paused = await PausedRun.StartAsync(_workspace, new FakeTriageModel());

        var result = paused.First.Results.Results.First();

        result.EvidenceUsed.ShouldBe(["src/Notifier.cs", "src/OrderService.cs", "src/Pricing.cs"]);
        result.EvidenceWithheld.ShouldBe([".ampelum-conventions.yaml", "backlog.md", "roadmap.md", "skills/skills.json", "triage-policy.json"]);
    }
}

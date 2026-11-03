using System.Text.Json;
using Ampelum.Samples.AgentRuntime;
using Shouldly;
using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Agent.Contracts;
using Xunit;

namespace Ampelum.Samples.AgentRuntime.Tests;

public sealed class GovernanceTests : IDisposable
{
    readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task ExhaustedCallBudget_RefusesTheCallsThatRemain_AndDefersTheIssueThatLostOut()
    {
        var model = new FakeTriageModel();
        var policy = TriagePolicy.Load(_workspace.Root) with { MaxCalls = 7 };

        var result = await TriageScenario.RunAsync(_workspace.Directory, model, FakeTriageModel.Tiers, policy: policy, ct: TestContext.Current.CancellationToken);

        // Planning is not part of the session budget: two calls. Each issue is one tool call plus two model calls, so
        // seven calls cover two issues and the tool call of the third; its first model call is refused.
        model.Calls.Count(IsBudgeted).ShouldBe(4);
        result.Outcome.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        result.Outcome.Assessed.Results.Count.ShouldBe(2);
        var deferred = result.Outcome.Assessed.Deferred.ShouldHaveSingleItem();
        deferred.Reason.ShouldContain("Session budget used up after 7 calls");
        result.Outcome.Assessed.Score.ShouldBe(2d / 3d, 0.001);
        result.Outcome.EditorNotes.ShouldContain(note => note.StartsWith("Editor stopped:", StringComparison.Ordinal));
        File.ReadAllText(result.Outcome.ReportPath!).ShouldContain($"- #{deferred.Issue}: Session budget used up");
    }

    [Fact]
    public void TokenLimit_RefusesTheNextCallOnceTheBookedTokensReachIt()
    {
        var budget = new ModelBudget(new SessionBudget { MaxTokens = 250 });

        for (var call = 0; call < 3; call++)
        {
            budget.TryReserve().ShouldBeTrue();
            budget.AddTokens(100);
        }

        budget.IsExhausted.ShouldBeTrue();
        budget.TryReserve().ShouldBeFalse();
        (budget.Calls, budget.Tokens, budget.Refused).ShouldBe((3, 300, 1));
    }

    [Fact]
    public async Task PostReport_IsRefusedWithoutAGrant_AndRunsWithOne()
    {
        var caller = Caller();
        var grants = new SessionGrantStore();
        var dispatcher = Dispatcher(caller, grants);

        var refused = await dispatcher.InvokeAsync(TriageTools.PostReport, ReportArguments(), caller, TestContext.Current.CancellationToken);

        refused.Success.ShouldBeFalse();
        refused.Error.ShouldNotBeNull().ShouldContain("Confirmation required");
        File.Exists(ReportPath).ShouldBeFalse();

        grants.Add(new SessionGrant("grant-1", caller.InstanceId, TriageTools.PostReport, PolicyDecision.Allowed, DateTimeOffset.UtcNow.AddMinutes(5), "test"));
        var allowed = await dispatcher.InvokeAsync(TriageTools.PostReport, ReportArguments(), caller, TestContext.Current.CancellationToken);

        allowed.Success.ShouldBeTrue();
        File.ReadAllText(ReportPath).ShouldBe("report text");
    }

    [Fact]
    public async Task ToolCall_WhenTheSharedBudgetIsUsedUp_IsRefusedAndTheReportIsNotWritten()
    {
        var caller = Caller();
        var grants = new SessionGrantStore();
        grants.Add(new SessionGrant("grant-1", caller.InstanceId, TriageTools.PostReport, PolicyDecision.Allowed, DateTimeOffset.UtcNow.AddMinutes(5), "test"));
        var budget = new ModelBudget(new SessionBudget { MaxCallCount = 2 });
        var dispatcher = TriageTools.CreateDispatcher(
            caller, grants, DemoProject.ReadBacklog(_workspace.Root),
            (_, _) => throw new InvalidOperationException("The audit tool is not used here."),
            new ReportWriter(_workspace.Root, new SoftwareDomainProfile().BuildBlastRadius(_workspace.Root)),
            budget.Meter);
        budget.TryReserve().ShouldBeTrue(); // a model call elsewhere in the session used one of the two

        var first = await dispatcher.InvokeAsync(TriageTools.PostReport, ReportArguments(), caller, TestContext.Current.CancellationToken);
        File.Delete(ReportPath);
        var second = await dispatcher.InvokeAsync(TriageTools.PostReport, ReportArguments(), caller, TestContext.Current.CancellationToken);

        first.Success.ShouldBeTrue();
        second.Success.ShouldBeFalse();
        second.Error.ShouldNotBeNull().ShouldContain("Session budget exhausted");
        File.Exists(ReportPath).ShouldBeFalse();
        (budget.Calls, budget.Refused).ShouldBe((2, 1));
    }

    [Fact]
    public async Task PostReport_IgnoresAnExpiredGrantAndAGrantOfAnotherCaller()
    {
        var caller = Caller();
        var grants = new SessionGrantStore();
        grants.Add(new SessionGrant("expired", caller.InstanceId, TriageTools.PostReport, PolicyDecision.Allowed, DateTimeOffset.UtcNow.AddSeconds(-1), "test"));
        grants.Add(new SessionGrant("foreign", "another-instance", TriageTools.PostReport, PolicyDecision.Allowed, DateTimeOffset.UtcNow.AddMinutes(5), "test"));

        var result = await Dispatcher(caller, grants).InvokeAsync(TriageTools.PostReport, ReportArguments(), caller, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        File.Exists(ReportPath).ShouldBeFalse();
    }

    [Fact]
    public async Task ReportStep_OutsideItsToolAllowList_FailsTheRunAndWritesNothing()
    {
        await using var paused = await PausedRun.StartAsync(_workspace, new FakeTriageModel(), adjust: definition => definition with
        {
            Steps = [.. definition.Steps.Select(step => step.Kind == WorkflowStepKind.Tool ? step with { ToolAllowList = [TriageTools.Audit] } : step)],
        });

        var outcome = await paused.ApproveAndFinishAsync();

        outcome.Status.ShouldBe(WorkflowExecutionStatus.Failed);
        outcome.FailureReason.ShouldNotBeNull().ShouldContain("allow-list");
        outcome.ReportPath.ShouldBeNull();
        File.Exists(ReportPath).ShouldBeFalse();
    }

    [Fact]
    public async Task BlastRadius_KeepsReadsInsideTheReadableFolder_AndWritesInsideTheWorkspace()
    {
        await using var process = _workspace.NewProcess(new FakeTriageModel());

        process.Readable.ContainsPath(Path.Combine(_workspace.Root, "src", "Pricing.cs")).ShouldBeTrue();
        process.Readable.ContainsPath(Path.Combine(_workspace.Root, "src-old", "Pricing.cs")).ShouldBeFalse();
        process.Readable.ContainsPath(Path.Combine(_workspace.Root, "backlog.md")).ShouldBeFalse();
        process.Readable.ContainsTool(TriageTools.Audit).ShouldBeTrue();
        process.Readable.ContainsTool(TriageTools.PostReport).ShouldBeFalse();

        var elsewhere = new ReportWriter(Path.Combine(_workspace.Directory, "elsewhere"), process.Workspace.MaxBlastRadius);
        await Should.ThrowAsync<InvalidOperationException>(() => elsewhere.WriteAsync("x", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("../project-old")]
    [InlineData("../project-old/src")]
    public void ReadablePath_ThatClimbsOutOfTheWorkspace_IsRejected(string escape)
    {
        var policy = TriagePolicy.Load(_workspace.Root) with { ReadablePaths = ["src", escape] };

        Should.Throw<InvalidOperationException>(() => _workspace.NewProcess(new FakeTriageModel(), policy))
            .Message.ShouldContain("outside the workspace blast radius");
    }

    [Fact]
    public async Task Auditor_RefusesToReadAFileOutsideItsOwnScope_EvenWhenTheFactsListedIt()
    {
        await using var process = _workspace.NewProcess(new FakeTriageModel());
        var narrow = new BlastRadiusScope { AllowedResourcePaths = new HashSet<string> { Path.Combine(_workspace.Root, "docs") + Path.DirectorySeparatorChar } };
        var results = new AuditResults();
        var auditor = new IssueAuditor(
            _workspace.Root, "exec", process.Policy, narrow, process.Facts, process.Skills,
            process.ClientFor, process.Journal, process.Budget, results);

        var failure = await Should.ThrowAsync<InvalidOperationException>(() =>
            auditor.AuditAsync(DemoProject.ReadBacklog(_workspace.Root)[0], TestContext.Current.CancellationToken));

        failure.Message.ShouldContain("outside the readable scope");
        results.Results.ShouldBeEmpty();
    }

    string ReportPath => Path.Combine(_workspace.Root, ReportWriter.FileName);

    static bool IsBudgeted(ModelCall call) =>
        !call.Prompt.Contains("Decompose the intent", StringComparison.Ordinal)
        && !call.Prompt.Contains("Create a workflow YAML", StringComparison.Ordinal);

    static AgentIdentity Caller() =>
        new("triage-test", Guid.NewGuid().ToString("N"), ["triage"], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));

    ToolDispatcher Dispatcher(AgentIdentity caller, SessionGrantStore grants) =>
        TriageTools.CreateDispatcher(
            caller, grants, DemoProject.ReadBacklog(_workspace.Root),
            (_, _) => throw new InvalidOperationException("The audit tool is not used here."),
            new ReportWriter(_workspace.Root, new SoftwareDomainProfile().BuildBlastRadius(_workspace.Root)));

    static Dictionary<string, JsonElement> ReportArguments() =>
        new() { [TriageTools.ContentArgument] = JsonSerializer.SerializeToElement("report text") };
}

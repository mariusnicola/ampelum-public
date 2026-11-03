using Ampelum.Samples.AgentRuntime;
using Shouldly;
using SignalGrid.Ampelum.Agent;
using Xunit;

namespace Ampelum.Samples.AgentRuntime.Tests;

public sealed class MemoryTests : IDisposable
{
    readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task PhaseNotes_KeepTheGoalAndTheFinishedPhase_WhenAnotherInstanceReadsTheSameFolder()
    {
        var folder = Path.Combine(_workspace.State, "notes-only");
        var first = new PhaseNotes(new JsonFileStateStore(folder));
        await first.OpenAsync("run-1", "Triage the backlog", "intake", TestContext.Current.CancellationToken);
        await first.ArchiveAsync("run-1", "refine", TestContext.Current.CancellationToken);

        var restarted = new PhaseNotes(new JsonFileStateStore(folder));
        var context = await restarted.ContextAsync("run-1", TestContext.Current.CancellationToken);

        context.Select(m => m.Content).ShouldContain(c => c.Contains("Triage the backlog", StringComparison.Ordinal));
        context.Select(m => m.Content).ShouldContain(c => c.StartsWith("Completed phases:", StringComparison.Ordinal) && c.Contains("intake", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PhaseNotes_OverTheInMemoryStore_BehaveTheSame_AndOpeningTwiceKeepsTheFirstGoal()
    {
        var notes = new PhaseNotes(new InMemoryStateStore());
        await notes.OpenAsync("run-1", "First goal", "intake", TestContext.Current.CancellationToken);
        await notes.OpenAsync("run-1", "Second goal", "intake", TestContext.Current.CancellationToken);

        var context = await notes.ContextAsync("run-1", TestContext.Current.CancellationToken);

        context.Select(m => m.Content).ShouldContain(c => c.Contains("First goal", StringComparison.Ordinal));
        context.Select(m => m.Content).ShouldNotContain(c => c.Contains("Second goal", StringComparison.Ordinal));
        await Should.ThrowAsync<InvalidOperationException>(() => notes.ContextAsync("unknown", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReportWrittenByTheSecondProcess_StillStatesTheGoalAndTheFinishedPhase()
    {
        var result = await TriageScenario.RunAsync(_workspace.Directory, new FakeTriageModel(), FakeTriageModel.Tiers, ct: TestContext.Current.CancellationToken);

        var report = File.ReadAllText(result.Outcome.ReportPath!);

        report.ShouldContain(TriageScenario.Goal);
        report.ShouldContain("Completed phases:");
        report.ShouldContain("Phase 'assess': completed");
    }

    [Fact]
    public async Task Journal_StoresAnIdenticalFileReadOnce_AndItsLedgerAddsUpToTheBudget()
    {
        var model = new FakeTriageModel();
        await using var paused = await PausedRun.StartAsync(_workspace, model);

        var rendered = await paused.First.Journal.RenderAsync(TestContext.Current.CancellationToken);
        var ledgerTokens = await paused.First.Journal.TotalTokensAsync(TestContext.Current.CancellationToken);

        // Issues 101 and 102 both read src/OrderService.cs; the second read becomes a placeholder.
        var orderService = File.ReadAllText(Path.Combine(_workspace.Root, "src", "OrderService.cs"));
        rendered.Count(m => m.Content.Contains(orderService, StringComparison.Ordinal)).ShouldBe(1);
        rendered.Select(m => m.Content).ShouldContain(c => c.Contains(TriageScenario.Goal, StringComparison.Ordinal));
        // The ledger books the assessments and the editor passes (5 calls of 100 tokens). The two skill-selection calls
        // go through the selector and are counted by the budget only.
        var selectionCalls = model.Calls.Count(c => c.Prompt.Contains("Select the best skill(s) for", StringComparison.Ordinal));
        selectionCalls.ShouldBe(2);
        ledgerTokens.ShouldBe(500);
        paused.First.Budget.Tokens.ShouldBe(ledgerTokens + selectionCalls * 100);
    }
}

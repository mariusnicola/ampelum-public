using Ampelum.Samples.AgentRuntime;
using Microsoft.Extensions.AI;
using Shouldly;
using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Agent.Execution;
using Xunit;

namespace Ampelum.Samples.AgentRuntime.Tests;

public sealed class SubAgentTests : IDisposable
{
    readonly TempWorkspace _workspace = new();

    string Pricing => Path.Combine(_workspace.Root, "src", "Pricing.cs");

    public void Dispose() => _workspace.Dispose();

    static ChatMessage Say(string text) => new(ChatRole.Assistant, text);

    static ChatMessage Write(string path, string content) =>
        FakeFixerModel.ToolCall("w", "write_file", ("path", path), ("content", content), ("append", null));

    static ChatMessage Read(string path) => FakeFixerModel.ToolCall("r", "read_file", ("path", path), ("offset", null), ("limit", null));

    [Fact]
    public async Task Fix_WhenTheSharedBudgetIsUsedUp_IsRefusedBeforeTheModelRunsAndTheFileIsUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = File.ReadAllText(Pricing);
        var model = new FakeFixerModel(FakeFixerModel.PricingFix());
        var budget = new ModelBudget(new SessionBudget { MaxCallCount = 1 });
        budget.TryReserve().ShouldBeTrue(); // the one call the session may make is already spent

        var fix = await IssueFixer.FixAsync(_workspace.Root, model, ct: ct, budget: budget.Meter);

        fix.Passed.ShouldBeFalse();
        fix.Run.Outcome.ShouldBe("Session budget exhausted.");
        fix.Detail.ShouldContain("Session budget exhausted");
        model.Calls.ShouldBe(0);
        File.ReadAllText(Pricing).ShouldBe(before);
        budget.Refused.ShouldBe(1);
    }

    [Fact]
    public async Task Fix_WritesTheFileThroughTheRunnersWriteTool()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = File.ReadAllText(Pricing);
        var model = new FakeFixerModel(FakeFixerModel.PricingFix());

        var fix = await IssueFixer.FixAsync(_workspace.Root, model, ct: ct);

        before.ShouldNotContain("Math.Round");
        File.ReadAllText(Pricing).ShouldContain("Math.Round");
        fix.Passed.ShouldBeTrue(fix.Detail);
        fix.Detail.ShouldContain("not compiled");
        fix.Run.FilesChanged.Select(Path.GetFullPath).ShouldBe([Pricing]);
        // The tool answered the model: its result went back into the next call.
        model.Prompts.ShouldContain(p => p.Contains("OK: wrote"));
        model.Prompts.ShouldContain(p => p.Contains("namespace Shop;") && p.Contains("[result read-1]"));
    }

    [Fact]
    public async Task Fix_ScriptWithoutTheWriteCall_LeavesTheFileAndFailsTheCheck()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = File.ReadAllText(Pricing);
        var model = new FakeFixerModel([Read(IssueFixer.PricingFile), Say("DONE: fixed it, trust me.")]);

        var fix = await IssueFixer.FixAsync(_workspace.Root, model, ct: ct);

        fix.Run.Success.ShouldBeTrue();
        File.ReadAllText(Pricing).ShouldBe(before);
        fix.Passed.ShouldBeFalse();
        fix.Detail.ShouldContain("did not record a write");
    }

    [Fact]
    public async Task Fix_EndsOnDone_AndMakesNoCallAfterIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var model = new FakeFixerModel([.. FakeFixerModel.PricingFix(), Say("DONE: this message must never be asked for.")]);

        var fix = await IssueFixer.FixAsync(_workspace.Root, model, ct: ct);

        fix.Run.Success.ShouldBeTrue();
        fix.Run.Outcome.ShouldStartWith("ApplyDiscount now rounds");
        fix.Run.Outcome.ShouldNotContain("DONE:");
        // read call, write call and the final answer: three calls, then the runner stops.
        model.Calls.ShouldBe(3);
    }

    [Fact]
    public async Task Fix_TextWithoutDone_RunsToTheTurnLimitAndFails()
    {
        var ct = TestContext.Current.CancellationToken;
        var model = new FakeFixerModel(Enumerable.Range(0, 10).Select(_ => Say("Still looking at it.")));

        var fix = await IssueFixer.FixAsync(_workspace.Root, model, maxTurns: 4, ct: ct);

        fix.Run.Success.ShouldBeFalse();
        fix.Run.Outcome.ShouldBe("Hit 4-turn limit.");
        model.Calls.ShouldBe(4);
        fix.Passed.ShouldBeFalse();
        fix.Detail.ShouldContain("did not end on DONE:");
    }

    [Fact]
    public async Task Fix_DefaultTurnLimitOfTheSample_IsSix()
    {
        var ct = TestContext.Current.CancellationToken;
        var model = new FakeFixerModel(Enumerable.Range(0, 20).Select(_ => Say("Thinking.")));

        var fix = await IssueFixer.FixAsync(_workspace.Root, model, ct: ct);

        IssueFixer.MaxTurns.ShouldBe(6);
        fix.Run.Outcome.ShouldBe("Hit 6-turn limit.");
        model.Calls.ShouldBe(6);
    }

    [Fact]
    public async Task Scenario_FixesTheIssueAfterTheReportIsPosted()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixer = new FakeFixerModel(FakeFixerModel.PricingFix());
        var log = new StringWriter();

        var result = await TriageScenario.RunAsync(_workspace.Directory, new FakeTriageModel(), FakeTriageModel.Tiers, log, ct: ct, fixer: fixer);

        result.Outcome.ReportPath.ShouldNotBeNull();
        result.Fix.ShouldNotBeNull().Passed.ShouldBeTrue();
        File.ReadAllText(Pricing).ShouldContain("Math.Round");
        log.ToString().ShouldContain("Sub-agent fix of #102: [OK]");
    }

    [Fact]
    public async Task Scenario_WithoutAFixer_LeavesThePricingFileAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = File.ReadAllText(Pricing);

        var result = await TriageScenario.RunAsync(_workspace.Directory, new FakeTriageModel(), FakeTriageModel.Tiers, ct: ct);

        result.Fix.ShouldBeNull();
        File.ReadAllText(Pricing).ShouldBe(before);
    }

    // The tests below pin what SubAgentRunner can reach. Every path they use sits inside this test's own temp folder.

    [Fact]
    public async Task AbsolutePathWrite_OutsideTheWorkingDirectory_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var outside = Path.Combine(_workspace.Directory, "outside", "absolute.txt");
        var model = new FakeFixerModel([Write(outside, "written outside the working directory"), Say("DONE: wrote.")]);

        var fix = await IssueFixer.FixAsync(_workspace.Root, model, ct: ct);

        fix.Run.Success.ShouldBeTrue();
        File.Exists(outside).ShouldBeFalse();
        Path.GetFullPath(outside).StartsWith(Path.GetFullPath(_workspace.Root), StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
        model.Prompts.ShouldContain(p => p.Contains("[result w] ERROR: path is outside the working directory"));
        fix.Run.FilesChanged.ShouldBeEmpty();
    }

    [Fact]
    public async Task DotDotPathWrite_OutsideTheWorkingDirectory_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var model = new FakeFixerModel([Write("../outside/dotdot.txt", "written through a parent segment"), Say("DONE: wrote.")]);

        await IssueFixer.FixAsync(_workspace.Root, model, ct: ct);

        File.Exists(Path.Combine(_workspace.Directory, "outside", "dotdot.txt")).ShouldBeFalse();
        Directory.Exists(Path.Combine(_workspace.Directory, "outside")).ShouldBeFalse();
    }

    [Fact]
    public async Task ReadFileAndGrep_OutsideTheWorkingDirectory_AreRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var outside = Path.Combine(_workspace.Directory, "elsewhere");
        Directory.CreateDirectory(outside);
        var secret = Path.Combine(outside, "note.txt");
        File.WriteAllText(secret, "needle-outside-the-working-directory");
        var model = new FakeFixerModel(
        [
            Read(secret),
            FakeFixerModel.ToolCall("g", "grep", ("pattern", "needle-outside"), ("basePath", outside), ("fileGlob", null)),
            Say("DONE: read."),
        ]);

        await IssueFixer.FixAsync(_workspace.Root, model, ct: ct);

        model.Prompts.ShouldContain(p => p.Contains("[result r] ERROR: path is outside the working directory"));
        model.Prompts.ShouldContain(p => p.Contains("[result g] ERROR: path is outside the working directory"));
        model.Prompts.ShouldNotContain(p => p.Contains("[result r] needle-outside"));
    }

    [Fact]
    public async Task ListDirectory_OutsideTheWorkingDirectory_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var outside = Path.Combine(_workspace.Directory, "listed");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "listed-marker.txt"), "x");
        var model = new FakeFixerModel(
        [
            FakeFixerModel.ToolCall("l", "list_directory", ("path", outside), ("pattern", null)),
            Say("DONE: listed."),
        ]);

        await IssueFixer.FixAsync(_workspace.Root, model, ct: ct);

        model.Prompts.ShouldContain(p => p.Contains("[result l] ERROR: path is outside the working directory"));
        model.Prompts.ShouldNotContain(p => p.Contains("listed-marker.txt"));
    }

    [Fact]
    public async Task Glob_OutsideTheWorkingDirectory_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var outside = Path.Combine(_workspace.Directory, "globbed");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "globbed-marker.txt"), "x");
        var model = new FakeFixerModel(
        [
            FakeFixerModel.ToolCall("g", "glob", ("pattern", "globbed-marker.*"), ("basePath", outside)),
            Say("DONE: globbed."),
        ]);

        await IssueFixer.FixAsync(_workspace.Root, model, ct: ct);

        model.Prompts.ShouldContain(p => p.Contains("[result g] ERROR: path is outside the working directory"));
        model.Prompts.ShouldNotContain(p => p.Contains("globbed-marker.txt"));
    }

    [Fact]
    public async Task WriteFileAppend_OutsideTheWorkingDirectory_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var outside = Path.Combine(_workspace.Directory, "appended.txt");
        File.WriteAllText(outside, "first;");
        var model = new FakeFixerModel(
        [
            FakeFixerModel.ToolCall("a", "write_file", ("path", outside), ("content", "second;"), ("append", true)),
            Say("DONE: appended."),
        ]);

        await IssueFixer.FixAsync(_workspace.Root, model, ct: ct);

        File.ReadAllText(outside).ShouldBe("first;");
    }

    [Fact]
    public async Task NoShellOrBuildTool_AndThePromptNamesOnlyTheToolsThatExist()
    {
        var ct = TestContext.Current.CancellationToken;
        var model = new FakeFixerModel([Say("DONE: nothing to do.")]);

        await IssueFixer.FixAsync(_workspace.Root, model, ct: ct);

        model.ToolNames[0].ShouldBe(["read_file", "write_file", "list_directory", "glob", "grep"], ignoreOrder: true);
        model.Prompts[0].ShouldContain("Tools: read_file, write_file, list_directory, glob, grep");
        model.Prompts[0].ShouldNotContain("build");
    }

    [Fact]
    public async Task ToolCallToABuildTool_IsNotRunAndTheRunnerCarriesOn()
    {
        var ct = TestContext.Current.CancellationToken;
        var model = new FakeFixerModel([FakeFixerModel.ToolCall("b", "build"), Say("DONE: gave up on the build.")]);

        var fix = await IssueFixer.FixAsync(_workspace.Root, model, ct: ct);

        fix.Run.Success.ShouldBeTrue();
        model.Prompts.ShouldContain(p => p.Contains("[result b] Error"));
        Directory.GetFiles(_workspace.Root, "*", SearchOption.AllDirectories).Any(f => f.Contains("bin", StringComparison.OrdinalIgnoreCase)).ShouldBeFalse();
    }

    [Fact]
    public async Task ARawClient_HasItsToolCallsRunByTheRunner()
    {
        var ct = TestContext.Current.CancellationToken;
        var model = new FakeFixerModel(FakeFixerModel.PricingFix());

        // The client given is the raw scripted model; the runner runs the calls itself.
        var fix = await IssueFixer.FixAsync(_workspace.Root, model, ct: ct);

        fix.Run.Success.ShouldBeTrue();
        fix.Run.FilesChanged.Select(Path.GetFullPath).ShouldBe([Pricing]);
        File.ReadAllText(Pricing).ShouldContain("Math.Round");
        fix.Passed.ShouldBeTrue(fix.Detail);
    }

    [Fact]
    public async Task TheFixAsksForTheCapableTier_AndRunsOnTheClientThatTierMapsTo()
    {
        var ct = TestContext.Current.CancellationToken;
        var fallback = new FakeFixerModel([Say("DONE: the wrong client ran.")]);
        var capable = new FakeFixerModel(FakeFixerModel.PricingFix());
        var asked = new List<ModelTier>();

        var fix = await IssueFixer.FixAsync(_workspace.Root, fallback, ct: ct, clientForTier: tier =>
        {
            asked.Add(tier);
            return capable;
        });

        asked.ShouldBe([ModelTier.Capable]);
        fallback.Calls.ShouldBe(0);
        capable.Calls.ShouldBe(3);
        fix.Passed.ShouldBeTrue(fix.Detail);
    }

    [Fact]
    public async Task FailedWrite_IsNotListedAsAChangedFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var directoryAsFile = Path.Combine(_workspace.Root, "src");
        var model = new FakeFixerModel([Write(directoryAsFile, "cannot be written"), Say("DONE: tried.")]);

        var fix = await IssueFixer.FixAsync(_workspace.Root, model, ct: ct);

        model.Prompts.ShouldContain(p => p.Contains("[result w] ERROR:"));
        fix.Run.FilesChanged.ShouldBeEmpty();
    }
}

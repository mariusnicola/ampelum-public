using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Ampelum.Samples.AgentService.Tests;

public sealed class ReplAndCliTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ampelum-agent-service-cli-" + Guid.NewGuid().ToString("N"));

    public ReplAndCliTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Repl_RunsAScriptAgainstTheModel_AndPrintsItsReply()
    {
        var model = FakeScript.ReplModel("Order A-1001 has shipped.");

        var (output, error) = await ReplSession.RunAsync(model, ReplSession.AskScript, Ct);

        error.ShouldBeEmpty();
        ReplSession.LastValue(output).ShouldBe("Order A-1001 has shipped.");
        model.RecordedCalls.Single().Messages.Single().Text.ShouldBe("Where is order A-1001?");
    }

    [Fact]
    public async Task Repl_ScriptWithoutAValue_LeavesNothingToRead()
    {
        var (output, error) = await ReplSession.RunAsync(FakeScript.ReplModel(), "var n = 1;\n:quit\n", Ct);

        error.ShouldBeEmpty();
        ReplSession.LastValue(output).ShouldBeEmpty();
    }

    [Fact]
    public async Task Repl_ReportsAScriptErrorOnTheErrorWriter()
    {
        var (_, error) = await ReplSession.RunAsync(FakeScript.ReplModel(), "this is not C#\n:quit\n", Ct);

        error.ShouldStartWith("error:");
    }

    [Fact]
    public async Task CliEval_ScoresPredictionsAgainstExpectedValues()
    {
        var dataset = Path.Combine(_dir, "dataset.jsonl");
        await CliEval.WriteDatasetAsync(dataset,
        [
            new EvalRow("q1", "3 to 5 days", "3 to 5 days"),
            new EvalRow("q2", "3 to 5 days", "never"),
        ], Ct);

        var (exit, output) = await CliEval.RunAsync(dataset, "exact", Path.Combine(_dir, "report"), Ct);

        exit.ShouldBe(0, output);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_dir, "report", "report.json"), Ct));
        var rows = report.RootElement.GetProperty("Rows").EnumerateArray().ToArray();
        rows.Select(r => r.GetProperty("Score").GetDouble()).ShouldBe([1d, 0d]);
        report.RootElement.GetProperty("MeanScore").GetDouble().ShouldBe(0.5);
    }

    [Fact]
    public async Task CliEval_MissingDataset_FailsWithExitCode2()
    {
        var (exit, output) = await CliEval.RunAsync(
            Path.Combine(_dir, "absent.jsonl"), "exact", Path.Combine(_dir, "report"), Ct);

        exit.ShouldBe(2);
        output.ShouldContain("Dataset not found");
    }

    [Fact]
    public async Task CliEval_RunsTheRealCliAndWritesItsReport()
    {
        var dataset = Path.Combine(_dir, "dataset.jsonl");
        await CliEval.WriteDatasetAsync(dataset, [new EvalRow("q", "a", "a")], Ct);

        var (exit, _) = await CliEval.RunAsync(dataset, "exact", Path.Combine(_dir, "report"), Ct);

        exit.ShouldBe(0);
        File.Exists(Path.Combine(_dir, "report", "report.json")).ShouldBeTrue();
    }

    /// <summary>A child that cannot finish by itself, started directly with no shell.</summary>
    static ProcessStartInfo NeverEndingChild()
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping")
            : new ProcessStartInfo("sleep");
        foreach (var arg in OperatingSystem.IsWindows() ? new[] { "-n", "60", "127.0.0.1" } : ["60"])
            start.ArgumentList.Add(arg);
        return start;
    }

    /// <summary>The process is disposed when the run returns, so exit is observed through its event.</summary>
    static Task<bool> WatchExit(Process process)
    {
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => exited.TrySetResult(true);
        return exited.Task;
    }

    [Fact]
    public async Task RunProcess_ThatOutlivesItsLimit_IsKilledAndReportedAsATimeout()
    {
        Task<bool>? exited = null;

        var failure = await Should.ThrowAsync<TimeoutException>(
            CliEval.RunProcessAsync(NeverEndingChild(), TimeSpan.FromMilliseconds(200), Ct, p => exited = WatchExit(p))
                .WaitAsync(TimeSpan.FromSeconds(30), Ct));

        failure.Message.ShouldContain("was stopped");
        (await exited!.WaitAsync(TimeSpan.FromSeconds(30), Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task RunProcess_CancelledByTheCaller_IsKilledAndThrowsCancellation()
    {
        Task<bool>? exited = null;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = CliEval.RunProcessAsync(NeverEndingChild(), TimeSpan.FromSeconds(60), cancel.Token, p =>
        {
            exited = WatchExit(p);
            cancel.Cancel();
        });

        await Should.ThrowAsync<OperationCanceledException>(run.WaitAsync(TimeSpan.FromSeconds(30), Ct));
        (await exited!.WaitAsync(TimeSpan.FromSeconds(30), Ct)).ShouldBeTrue();
    }
}

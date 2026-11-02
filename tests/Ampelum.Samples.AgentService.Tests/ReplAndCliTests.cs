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
    public async Task CliEval_ThatOutlivesItsTimeout_IsStoppedAndReportedAsATimeout()
    {
        var dataset = Path.Combine(_dir, "dataset.jsonl");
        await CliEval.WriteDatasetAsync(dataset, [new EvalRow("q", "a", "a")], Ct);

        // A runtime cannot start and finish inside a millisecond, so the limit always fires first.
        var failure = await Should.ThrowAsync<TimeoutException>(() => CliEval.RunAsync(
            dataset, "exact", Path.Combine(_dir, "report"), Ct, TimeSpan.FromMilliseconds(1)));

        failure.Message.ShouldContain("was stopped");
        // A CLI left running would finish and write its report; a killed one never does.
        File.Exists(Path.Combine(_dir, "report", "report.json")).ShouldBeFalse();
    }

    [Fact]
    public async Task CliEval_CancelledByTheCaller_IsStoppedAndThrowsCancellation()
    {
        var dataset = Path.Combine(_dir, "dataset.jsonl");
        await CliEval.WriteDatasetAsync(dataset, [new EvalRow("q", "a", "a")], Ct);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancel.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => CliEval.RunAsync(
            dataset, "exact", Path.Combine(_dir, "report"), cancel.Token));
        File.Exists(Path.Combine(_dir, "report", "report.json")).ShouldBeFalse();
    }
}

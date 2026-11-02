using System.Text.Json;
using SignalGrid.Ampelum.Cli.Commands;
using SignalGrid.Ampelum.Cli.Reporting;
using Shouldly;
using Xunit;

namespace Ampelum.Cli.Tests;

public class EvalCommandTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ampelum-eval-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private string WriteDataset()
    {
        Directory.CreateDirectory(_tempDir);
        var ds = Path.Combine(_tempDir, "data.jsonl");
        File.WriteAllLines(ds, [
            """{"input":"a","expected":"yes","predicted":"yes"}""",
            """{"input":"b","expected":"yes","predicted":"no"}""",
            """{"input":"c","expected":"42","predicted":"42"}""",
        ]);
        return ds;
    }

    [Fact]
    public async Task Eval_WritesMarkdownAndJson()
    {
        var ds = WriteDataset();
        var outDir = Path.Combine(_tempDir, "out");
        var runner = new RecordingProcessRunner();
        var config = RootCommandFactory.Create(runner);

        var (exit, stdout, _) = await StdoutCapture.RunAsync(
            config,
            $"eval --signature MyApp.QA --dataset {ds} --metric exact --output {outDir}",
            TestContext.Current.CancellationToken);

        exit.ShouldBe(0);
        stdout.ShouldContain("Wrote eval report");

        var md = await File.ReadAllTextAsync(Path.Combine(outDir, "report.md"), TestContext.Current.CancellationToken);
        md.ShouldContain("| metric | score |");
        md.ShouldContain("exact[0]");
        md.ShouldContain("exact[2]");
        md.ShouldContain("Mean score");

        var jsonText = await File.ReadAllTextAsync(Path.Combine(outDir, "report.json"), TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(jsonText);
        doc.RootElement.GetProperty("Signature").GetString().ShouldBe("MyApp.QA");
        doc.RootElement.GetProperty("Rows").GetArrayLength().ShouldBe(3);
        doc.RootElement.GetProperty("MeanScore").GetDouble().ShouldBe(2.0 / 3.0, tolerance: 1e-6);
    }

    [Fact]
    public async Task Eval_MissingDataset_ReturnsExit2()
    {
        var runner = new RecordingProcessRunner();
        var config = RootCommandFactory.Create(runner);

        var (exit, _, stderr) = await StdoutCapture.RunAsync(
            config,
            $"eval --signature S --dataset {Path.Combine(_tempDir, "nope.jsonl")} --metric exact --output {_tempDir}/out",
            TestContext.Current.CancellationToken);

        exit.ShouldBe(2);
        stderr.ShouldContain("Dataset not found");
    }

    [Fact]
    public void SimpleMetric_F1_ScoresTokenOverlap()
    {
        SimpleMetric.Score("f1", "the quick brown fox", "the quick brown fox").ShouldBe(1d);
        SimpleMetric.Score("f1", "the quick brown fox", "the slow brown fox").ShouldBe(0.75d, tolerance: 1e-6);
        SimpleMetric.Score("f1", "alpha", "beta").ShouldBe(0d);
        SimpleMetric.Score("f1", "", "x").ShouldBe(0d);
    }

    [Fact]
    public void SimpleJsonlRow_MissingFields_DefaultEmpty()
    {
        var (i, e, p) = SimpleJsonlRow.Parse("{}");
        i.ShouldBe(string.Empty);
        e.ShouldBe(string.Empty);
        p.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task Eval_EmptyDataset_MeanZero()
    {
        Directory.CreateDirectory(_tempDir);
        var ds = Path.Combine(_tempDir, "empty.jsonl");
        File.WriteAllText(ds, "\n\n");
        var outDir = Path.Combine(_tempDir, "out");
        var runner = new RecordingProcessRunner();
        var config = RootCommandFactory.Create(runner);

        var (exit, _, _) = await StdoutCapture.RunAsync(
            config,
            $"eval --signature S --dataset {ds} --metric f1 --output {outDir}",
            TestContext.Current.CancellationToken);

        exit.ShouldBe(0);
        var jsonText = await File.ReadAllTextAsync(Path.Combine(outDir, "report.json"), TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(jsonText);
        doc.RootElement.GetProperty("MeanScore").GetDouble().ShouldBe(0d);
    }
}

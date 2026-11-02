using System.Globalization;
using SignalGrid.Ampelum.Cli.Reporting;

namespace SignalGrid.Ampelum.Cli.Commands;

/// <summary>
/// Drives the eval command: reads JSONL dataset, scores each row, writes markdown + JSON.
/// Plan deviation: v0.9d <c>Evaluate&lt;TSig&gt;</c> requires a real signature/IChatClient setup
/// at runtime. For the CLI surface we run the metric directly over precomputed predictions —
/// users who need live runs use <c>Evaluate&lt;TSig&gt;</c> from code. Documented in eval --help.
/// </summary>
internal static class EvalRunner
{
    public static async Task<int> RunAsync(EvalArgs args, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (!File.Exists(args.Dataset))
        {
            stderr.WriteLine($"Dataset not found: {args.Dataset}");
            return 2;
        }

        var rows = new List<EvalRow>();
        var idx = 0;
        foreach (var line in await File.ReadAllLinesAsync(args.Dataset, ct).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            var (input, expected, predicted) = SimpleJsonlRow.Parse(line);
            var score = SimpleMetric.Score(args.Metric, expected, predicted);
            rows.Add(new EvalRow(idx++, input, expected, predicted, score));
        }

        var report = new EvalReport(
            Signature: args.Signature,
            Metric: args.Metric,
            GeneratedAt: DateTimeOffset.UtcNow,
            Rows: rows,
            MeanScore: rows.Count == 0 ? 0d : rows.Average(r => r.Score));

        Directory.CreateDirectory(args.Output);
        await MarkdownReportWriter.WriteAsync(report, Path.Combine(args.Output, "report.md"), ct).ConfigureAwait(false);
        await JsonReportWriter.WriteAsync(report, Path.Combine(args.Output, "report.json"), ct).ConfigureAwait(false);
        stdout.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "Wrote eval report ({0} rows, mean={1:F3}) to {2}.",
            rows.Count,
            report.MeanScore,
            args.Output));
        return 0;
    }
}

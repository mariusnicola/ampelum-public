using System.Globalization;
using System.Text;

namespace SignalGrid.Ampelum.Cli.Reporting;

internal static class MarkdownReportWriter
{
    public static async Task WriteAsync(EvalReport report, string path, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("date: ").Append(report.GeneratedAt.ToString("u", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("signature: ").Append(report.Signature).Append('\n');
        sb.Append("metric: ").Append(report.Metric).Append('\n');
        sb.Append("n: ").Append(report.Rows.Count).Append('\n');
        sb.Append("---\n\n");
        sb.Append("# Eval Report\n\n");
        sb.Append("| metric | score |\n");
        sb.Append("|---|---|\n");
        foreach (var row in report.Rows)
        {
            sb.Append("| ").Append(report.Metric).Append('[').Append(row.Index).Append("] | ")
              .Append(row.Score.ToString("F3", CultureInfo.InvariantCulture)).Append(" |\n");
        }
        sb.Append("\n## Summary\n\n");
        sb.Append("Mean score: **")
          .Append(report.MeanScore.ToString("F3", CultureInfo.InvariantCulture))
          .Append("**\n");

        await File.WriteAllTextAsync(path, sb.ToString(), ct).ConfigureAwait(false);
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using SignalGrid.Ampelum.Metrics;

namespace SignalGrid.Ampelum.Evaluation;

/// <summary>Markdown + JSON renderer for an <see cref="EvaluationResult{TSig}"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public static class EvalReport
{
    /// <summary>
    /// Produces a (markdown, json) pair summarising mean, stddev, failure count, and the failure
    /// samples retained on the result.
    /// </summary>
    /// <param name="result">Evaluation result to render.</param>
    /// <param name="metricsForLabels">Metrics that produced the scores; used purely for labelling.</param>
    public static (string Markdown, string Json) Render<TSig>(
        EvaluationResult<TSig> result,
        IReadOnlyList<IMetric> metricsForLabels)
        where TSig : ISignature
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(metricsForLabels);

        var labels = metricsForLabels.Select(m => m is INamedMetric n ? n.Name : m.GetType().Name).ToList();
        var md = new StringBuilder();
        md.AppendLine("# Evaluation Report");
        md.AppendLine();
        md.Append("Metrics: ").AppendLine(string.Join(", ", labels));
        md.AppendLine();
        md.AppendLine("| Statistic | Value |");
        md.AppendLine("|---|---|");
        md.AppendLine(CultureInfo.InvariantCulture, $"| Examples | {result.Scores.Count} |");
        md.AppendLine(CultureInfo.InvariantCulture, $"| Mean | {result.Mean:0.0000} |");
        md.AppendLine(CultureInfo.InvariantCulture, $"| StdDev | {result.StdDev:0.0000} |");
        md.AppendLine(CultureInfo.InvariantCulture, $"| Failures | {result.FailureCount} |");
        if (result.FailureSamples.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("## Failure samples (lowest scoring)");
            md.AppendLine("| # | Score | Error |");
            md.AppendLine("|---|---|---|");
            for (var i = 0; i < result.FailureSamples.Count; i++)
            {
                var s = result.FailureSamples[i];
                md.AppendLine(CultureInfo.InvariantCulture, $"| {i + 1} | {s.Score:0.0000} | {s.Error?.Message ?? ""} |");
            }
        }

        var json = JsonSerializer.Serialize(new
        {
            metrics = labels,
            examples = result.Scores.Count,
            mean = result.Mean,
            stddev = result.StdDev,
            failures = result.FailureCount,
            failureSamples = result.FailureSamples.Select(s => new { score = s.Score, error = s.Error?.Message }).ToList(),
        });
        return (md.ToString(), json);
    }
}

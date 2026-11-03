using System.Text;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Evaluation;
using SignalGrid.Ampelum.Metrics;

namespace Ampelum.Samples.ComposedPrograms.Evaluation;

/// <summary>Mean score of one metric over the approved notes of the previous release.</summary>
public sealed record MetricScore(string Metric, double Mean, double StdDev, int Failures);

public sealed record EvaluationSummary(IReadOnlyList<MetricScore> Scores, string Markdown, string Json)
{
    public double Mean(string metric) => Scores.Single(s => s.Metric == metric).Mean;
}

/// <summary>
/// Runs a program over the approved notes in the CSV and scores it with metrics chosen by name from the
/// registry. The approved notes are the only labels; a new release has none.
/// </summary>
public sealed class ReleaseEvaluation(IMetricRegistry registry, ChangeSource source)
{
    public Task<EvaluationSummary> ClassifyAsync(
        Func<ClassifySig, CancellationToken, Task<Prediction<ClassifySig>>> program,
        IReadOnlyList<string> metricNames, CancellationToken ct = default) =>
        RunAsync(program, metricNames, ct);

    public Task<EvaluationSummary> DraftAsync(
        Func<DraftSig, CancellationToken, Task<Prediction<DraftSig>>> program,
        IReadOnlyList<string> metricNames, CancellationToken ct = default) =>
        RunAsync(program, metricNames, ct);

    async Task<EvaluationSummary> RunAsync<TSig>(
        Func<TSig, CancellationToken, Task<Prediction<TSig>>> program,
        IReadOnlyList<string> metricNames, CancellationToken ct) where TSig : ISignature
    {
        var scores = new List<MetricScore>();
        var markdown = new StringBuilder();
        var json = new List<string>();
        foreach (var name in metricNames)
        {
            var metric = registry.Get(name) as IMetric<TSig>
                ?? throw new InvalidOperationException($"Metric {name} does not score {typeof(TSig).Name}.");
            var result = await new Evaluate<TSig>(program, metric, maxDegreeOfParallelism: 1)
                .RunAsync(source.Approved(), ct).ConfigureAwait(false);
            scores.Add(new MetricScore(name, result.Mean, result.StdDev, result.FailureCount));
            var (md, js) = EvalReport.Render(result, [metric]);
            markdown.Append("## ").AppendLine(name).AppendLine(md);
            json.Add(js);
        }
        return new EvaluationSummary(scores, markdown.ToString(), "[" + string.Join(",", json) + "]");
    }
}

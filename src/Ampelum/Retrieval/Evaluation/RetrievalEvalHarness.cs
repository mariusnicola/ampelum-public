using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using SignalGrid.Ampelum.Evaluation;

namespace SignalGrid.Ampelum.Retrieval.Evaluation;

/// <summary>
/// Drives an <see cref="IRetriever"/> over an <see cref="IDataset"/> (rows must contain
/// <c>query: string</c> and <c>gold_ids: string[]</c> / JSON-array string / CSV string) and
/// aggregates per-metric mean across rows.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class RetrievalEvalHarness
{
    readonly IRetriever _retriever;
    readonly IReadOnlyList<IRetrievalMetric> _metrics;
    readonly int _topK;
    readonly int _maxDop;

    /// <summary>Initialises the harness.</summary>
    /// <param name="retriever">Retriever to evaluate.</param>
    /// <param name="metrics">One or more retrieval metrics.</param>
    /// <param name="topK">Top-K to request from the retriever per row.</param>
    /// <param name="maxDegreeOfParallelism">Maximum concurrent rows.</param>
    public RetrievalEvalHarness(
        IRetriever retriever,
        IReadOnlyList<IRetrievalMetric> metrics,
        int topK = 10,
        int maxDegreeOfParallelism = 8)
    {
        ArgumentNullException.ThrowIfNull(retriever);
        ArgumentNullException.ThrowIfNull(metrics);
        if (metrics.Count == 0) throw new ArgumentException("at least one metric required", nameof(metrics));
        if (topK <= 0) throw new ArgumentOutOfRangeException(nameof(topK));
        _retriever = retriever;
        _metrics = metrics;
        _topK = topK;
        _maxDop = maxDegreeOfParallelism;
    }

    /// <summary>Runs the harness over the dataset and returns per-metric means.</summary>
    public async Task<RetrievalEvalResult> RunAsync(IDataset dataset, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        var rows = new List<(string query, string[] gold)>();
        await foreach (var row in dataset.EnumerateAsync(ct).ConfigureAwait(false))
        {
            var query = row.TryGetValue("query", out var q) ? q?.ToString() ?? "" : "";
            var gold = ParseIds(row.TryGetValue("gold_ids", out var g) ? g : null);
            rows.Add((query, gold));
        }

        var sums = new double[_metrics.Count];
        var lockObj = new object();
        await Parallel.ForEachAsync(rows,
            new ParallelOptions { MaxDegreeOfParallelism = _maxDop, CancellationToken = ct },
            async (item, t) =>
            {
                var docs = await _retriever.RetrieveAsync(item.query, _topK, null, t).ConfigureAwait(false);
                var ids = docs.Select(d => d.Id).ToArray();
                var local = new double[_metrics.Count];
                for (var i = 0; i < _metrics.Count; i++) local[i] = _metrics[i].Compute(ids, item.gold);
                lock (lockObj) for (var i = 0; i < local.Length; i++) sums[i] += local[i];
            }).ConfigureAwait(false);

        var means = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var i = 0; i < _metrics.Count; i++) means[_metrics[i].Name] = rows.Count == 0 ? 0 : sums[i] / rows.Count;
        return new RetrievalEvalResult(rows.Count, means);
    }

    internal static string[] ParseIds(object? raw) => raw switch
    {
        null => Array.Empty<string>(),
        string s when s.StartsWith('[') => JsonSerializer.Deserialize<string[]>(s) ?? Array.Empty<string>(),
        string s => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        IEnumerable<object?> e => e.Select(x => x?.ToString() ?? "").ToArray(),
        _ => new[] { raw.ToString() ?? "" },
    };
}

/// <summary>Per-metric mean result of a <see cref="RetrievalEvalHarness"/> run.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record RetrievalEvalResult(int Examples, IReadOnlyDictionary<string, double> Means);

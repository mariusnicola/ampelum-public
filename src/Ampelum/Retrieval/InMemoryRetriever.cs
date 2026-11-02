using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SignalGrid.Ampelum.Retrieval;

/// <summary>
/// BM25-lite keyword retriever for tests and small corpora.
/// Tokenises query and documents by whitespace and punctuation, computes term-frequency overlap,
/// and ranks by score. No IDF weighting â€” corpus-wide statistics are not required (YAGNI).
/// Supports equality filters on <see cref="RetrievedDocument.Metadata"/> keys.
/// Emits a <c>ampelum.retriever.query</c> activity with the required observability tags (see spec Â§6.1.2).
/// Thread-safe; the corpus is immutable after construction.
/// </summary>
public sealed class InMemoryRetriever : IRetriever
{
    static readonly Regex TokenPattern = new(@"[\w]+", RegexOptions.Compiled);

    readonly IReadOnlyList<RetrievedDocument> _corpus;
    readonly IReadOnlyList<string[]> _tokenised; // parallel to _corpus

    /// <summary>Constructs an <see cref="InMemoryRetriever"/> over the given corpus.</summary>
    public InMemoryRetriever(IReadOnlyList<RetrievedDocument> corpus)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        _corpus = corpus;
        var tok = new string[corpus.Count][];
        for (var i = 0; i < corpus.Count; i++)
            tok[i] = Tokenise(corpus[i].Text);
        _tokenised = tok;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<RetrievedDocument>> RetrieveAsync(
        string query,
        int topK,
        IReadOnlyDictionary<string, object?>? filters = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.retriever.query");
        activity?.SetTag("retriever.type", nameof(InMemoryRetriever));
        activity?.SetTag("query.topk", topK);

        var queryTokens = new HashSet<string>(Tokenise(query), StringComparer.OrdinalIgnoreCase);
        var scored = new List<(RetrievedDocument Doc, double Score)>(_corpus.Count);

        for (var i = 0; i < _corpus.Count; i++)
        {
            var doc = _corpus[i];
            if (filters is not null && !MatchesFilters(doc.Metadata, filters)) continue;
            var overlap = _tokenised[i].Count(t => queryTokens.Contains(t));
            if (overlap > 0)
                scored.Add((doc, (double)overlap));
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        var results = scored.Count <= topK
            ? scored.Select(s => s.Doc).ToList()
            : scored.Take(topK).Select(s => s.Doc).ToList();

        sw.Stop();
        var latency = sw.Elapsed.TotalMilliseconds;
        activity?.SetTag("query.latency_ms", latency);
        activity?.SetTag("query.hit_count", (long)results.Count);

        Diagnostics.RetrieverQueryDuration.Record(latency,
            new KeyValuePair<string, object?>("retriever.type", nameof(InMemoryRetriever)));
        Diagnostics.RetrieverQueryHits.Record(results.Count,
            new KeyValuePair<string, object?>("retriever.type", nameof(InMemoryRetriever)));

        return Task.FromResult<IReadOnlyList<RetrievedDocument>>(results);
    }

    static string[] Tokenise(string text) =>
        TokenPattern.Matches(text).Select(m => m.Value.ToLowerInvariant()).ToArray();

    static bool MatchesFilters(IReadOnlyDictionary<string, object?> metadata,
        IReadOnlyDictionary<string, object?> filters)
    {
        foreach (var kv in filters)
        {
            if (!metadata.TryGetValue(kv.Key, out var val)) return false;
            if (!Equals(val, kv.Value)) return false;
        }
        return true;
    }
}

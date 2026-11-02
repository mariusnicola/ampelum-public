using System.Collections.Immutable;

namespace SignalGrid.Ampelum.Retrieval;

/// <summary>
/// In-memory <see cref="IVectorStore"/> backed by an immutable snapshot for lock-free reads.
/// Brute-force cosine similarity; scores normalised to [0, 1] via <c>(cosine + 1) / 2</c>.
/// Thread-safe: concurrent queries run fully in parallel (lock-free snapshot read);
/// concurrent upserts are serialised via a private <see cref="SemaphoreSlim"/>.
/// Intended for tests and small corpora only.
/// </summary>
public sealed class InMemoryVectorStore : IVectorStore
{
    sealed record Entry(
        string Id,
        ReadOnlyMemory<float> Vector,
        IReadOnlyDictionary<string, object?> Metadata,
        RetrievedDocument Document);

    volatile ImmutableList<Entry> _snapshot = ImmutableList<Entry>.Empty;
    readonly SemaphoreSlim _mutationLock = new(1, 1);

    /// <inheritdoc/>
    public async Task UpsertAsync(
        IReadOnlyList<RetrievedDocument> documents,
        ReadOnlyMemory<float>[] vectors,
        CancellationToken ct = default)
    {
        if (documents.Count != vectors.Length)
            throw new ArgumentException(
                $"documents.Count ({documents.Count}) must equal vectors.Length ({vectors.Length}).",
                nameof(vectors));

        ct.ThrowIfCancellationRequested();
        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ids = new HashSet<string>(documents.Count);
            for (var i = 0; i < documents.Count; i++) ids.Add(documents[i].Id);

            var next = _snapshot
                .RemoveAll(e => ids.Contains(e.Id));
            for (var i = 0; i < documents.Count; i++)
                next = next.Add(new Entry(documents[i].Id, vectors[i], documents[i].Metadata, documents[i]));
            _snapshot = next;
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<(RetrievedDocument Document, double Score)>> QueryAsync(
        ReadOnlyMemory<float> queryVector,
        int topK,
        IReadOnlyDictionary<string, object?>? filters = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var snapshot = _snapshot; // single atomic reference read â€” no lock needed

        var scored = new List<(RetrievedDocument, double)>(snapshot.Count);
        foreach (var entry in snapshot)
        {
            if (filters is not null && !MatchesFilters(entry.Metadata, filters)) continue;
            var sim = CosineSimilarity(queryVector.Span, entry.Vector.Span);
            var normalised = (sim + 1.0) / 2.0;
            scored.Add((entry.Document, normalised));
        }

        scored.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        IReadOnlyList<(RetrievedDocument, double)> result =
            scored.Count <= topK ? scored : scored[..topK];
        return Task.FromResult(result);
    }

    /// <summary>Returns the current snapshot of upserted documents + their vectors. Internal â€” used by VectorIndexSerializer.</summary>
#pragma warning disable AMPELUM_V09_PREVIEW
    internal Ampelum.Retrieval.Persistence.VectorIndexSnapshot Snapshot()
    {
        var snapshot = _snapshot;
        var docs = new List<RetrievedDocument>(snapshot.Count);
        var vecs = new List<float[]>(snapshot.Count);
        foreach (var e in snapshot)
        {
            docs.Add(e.Document);
            vecs.Add(e.Vector.ToArray());
        }
        return new Ampelum.Retrieval.Persistence.VectorIndexSnapshot(docs, vecs);
    }
#pragma warning restore AMPELUM_V09_PREVIEW

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

    static double CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length == 0 || b.Length == 0) return 0.0;
        double dot = 0, normA = 0, normB = 0;
        var len = Math.Min(a.Length, b.Length);
        for (var i = 0; i < len; i++)
        {
            dot   += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        var denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom < 1e-12 ? 0.0 : dot / denom;
    }
}

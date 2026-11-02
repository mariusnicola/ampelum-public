namespace SignalGrid.Ampelum.Optimizers;

/// <summary>
/// Wraps an inner <typeparamref name="TProgram"/> compiled by <see cref="KnnFewShot{TProgram}"/>.
/// Implements <see cref="IComposedModule"/> by delegating to the inner program when it is
/// itself composed; otherwise reports an empty submodule map. Exposes
/// <see cref="ResolveNeighboursAsync"/> for runtime k-NN demo lookup.
/// </summary>
/// <typeparam name="TProgram">The wrapped program type.</typeparam>
public sealed class KnnBoundProgram<TProgram> : IComposedModule
    where TProgram : class
{
    readonly TProgram _inner;

    internal KnnConfig Config { get; }

    internal KnnBoundProgram(TProgram inner, KnnConfig config)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(config);
        _inner = inner;
        Config = config;
    }

    /// <summary>The original (unwrapped) program.</summary>
    public TProgram Inner => _inner;

    /// <summary>Number of neighbours requested at lookup time.</summary>
    public int K => Config.K;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object> Submodules =>
        _inner is IComposedModule c ? c.Submodules : new Dictionary<string, object>();

    /// <inheritdoc />
    public object Rebind(IReadOnlyDictionary<string, object> replacements) =>
        _inner is IComposedModule c
            ? new KnnBoundProgram<TProgram>((TProgram)c.Rebind(replacements), Config)
            : this;

    /// <summary>Returns up to <see cref="K"/> nearest training examples to <paramref name="queryText"/>.</summary>
    public async Task<IReadOnlyList<Example>> ResolveNeighboursAsync(
        string queryText, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(queryText);
        var embeddings = await Config.EmbeddingGenerator.GenerateAsync(
            [queryText], cancellationToken: ct).ConfigureAwait(false);
        var queryVec = embeddings[0].Vector;
        var hits = await Config.Store.QueryAsync(queryVec, Config.K, null, ct).ConfigureAwait(false);

        var result = new List<Example>(hits.Count);
        foreach (var (doc, _) in hits)
        {
            if (int.TryParse(doc.Id, out var idx) && idx >= 0 && idx < Config.AllExamples.Count)
                result.Add(Config.AllExamples[idx]);
        }
        return result;
    }
}

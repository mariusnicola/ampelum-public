using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SignalGrid.Ampelum.DependencyInjection;
using SignalGrid.Ampelum.Retrieval;

namespace Ampelum.Samples.VectorStoreSwap;

/// <summary>
/// Index documents and answer questions over whichever vector store the VECTOR_STORE setting names.
/// Everything except the registration in <see cref="VectorStoreRegistration"/> is store-agnostic.
/// </summary>
public sealed class VectorStoreSwapPipeline : IAsyncDisposable
{
    readonly ServiceProvider _provider;
    readonly IChatClient _chat;

    public VectorStoreSwapPipeline(
        Func<string, string> setting,
        IEmbeddingGenerator<string, Embedding<float>> embeddings,
        IChatClient chat,
        VectorBackends? backends = null)
    {
        _chat = chat;
        Store = VectorStoreKinds.Normalize(setting(SettingNames.VectorStore));

        var services = new ServiceCollection();
        services.AddSingleton(chat);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddAmpelum()
            .WithEmbeddingGenerator(_ => embeddings)
            .WithRetriever<EmbeddingRetriever>()
            .WithConfiguredVectorStore(setting, backends);
        _provider = services.BuildServiceProvider();
    }

    /// <summary>The store kind this pipeline was configured with.</summary>
    public string Store { get; }

    public async Task IndexAsync(IReadOnlyList<RetrievedDocument> documents, CancellationToken ct = default)
    {
        var embeddings = _provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        var generated = await embeddings.GenerateAsync(documents.Select(d => d.Text).ToList(), cancellationToken: ct);
        var vectors = generated.Select(e => e.Vector).ToArray();
        await _provider.GetRequiredService<IVectorStore>().UpsertAsync(documents, vectors, ct);
    }

    public async Task<StoreResult> RetrieveAsync(
        string question, int topK, IReadOnlyDictionary<string, object?>? filters = null, CancellationToken ct = default)
    {
        using var scope = _provider.CreateScope();
        var retriever = scope.ServiceProvider.GetRequiredService<IRetriever>();
        return new StoreResult(Store, await retriever.RetrieveAsync(question, topK, filters, ct));
    }

    public async Task<RagAdvisorResponse> AskAsync(string question, int topK, CancellationToken ct = default)
    {
        using var scope = _provider.CreateScope();
        var retriever = scope.ServiceProvider.GetRequiredService<IRetriever>();
        return await new RagAdvisor(retriever, _chat, new RagAdvisorOptions { TopK = topK }).AskAsync(question, ct);
    }

    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}

/// <summary>What one store returned for one query.</summary>
public sealed record StoreResult(string Store, IReadOnlyList<RetrievedDocument> Hits)
{
    public IReadOnlyList<string> Ids => Hits.Select(h => h.Id).ToArray();
}

/// <summary>Results of the same query run through several stores.</summary>
public sealed record ComparisonReport(IReadOnlyList<StoreResult> Results)
{
    const double ScoreTolerance = 1e-6;

    /// <summary>Every store returned the same ids in the same order.</summary>
    public bool RankingsMatch => Results.All(r => r.Ids.SequenceEqual(Results[0].Ids));

    /// <summary>
    /// Scores of the adapters for the real services, which all report cosine similarity. Null when fewer
    /// than two of them ran, because one store cannot disagree with itself. The in-memory store is left
    /// out: it does not fill the document score when read through a retriever.
    /// </summary>
    public bool? ScoresMatch
    {
        get
        {
            var scored = Results.Where(r => r.Store != VectorStoreKinds.InMemory).ToArray();
            if (scored.Length < 2)
                return null;
            return scored.All(r => r.Hits.Count == scored[0].Hits.Count
                && r.Hits.Zip(scored[0].Hits, (a, b) => Math.Abs(a.Score - b.Score) <= ScoreTolerance).All(ok => ok));
        }
    }
}

public static class StoreComparison
{
    /// <summary>Runs one query through every pipeline with identical arguments.</summary>
    public static async Task<ComparisonReport> RunAsync(
        IEnumerable<VectorStoreSwapPipeline> pipelines,
        string question,
        int topK,
        IReadOnlyDictionary<string, object?>? filters = null,
        CancellationToken ct = default)
    {
        var results = new List<StoreResult>();
        foreach (var pipeline in pipelines)
            results.Add(await pipeline.RetrieveAsync(question, topK, filters, ct));
        return new ComparisonReport(results);
    }
}

using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Retrieval.Hybrid;
using SignalGrid.Ampelum.Retrieval.Loaders;
using SignalGrid.Ampelum.Retrieval.Reranking;
using SignalGrid.Ampelum.Retrieval.Splitters;

namespace Ampelum.Samples.ResearchAssistant;

/// <summary>
/// The searchable document set. Markdown files are loaded, split into chunks and indexed twice, as keywords
/// (BM25) and as vectors. A query takes the fused top of both indexes and a reranker orders that pool.
/// </summary>
public sealed class KnowledgeBase : IDisposable
{
    readonly BM25Retriever _bm25;
    readonly IReranker _reranker;
    readonly int _pool;

    KnowledgeBase(
        IReadOnlyList<RetrievedDocument> chunks, BM25Retriever bm25, IRetriever vectors, IRetriever hybrid, IReranker reranker, int pool)
    {
        Chunks = chunks;
        _bm25 = bm25;
        Keywords = bm25;
        Vectors = vectors;
        Hybrid = hybrid;
        _reranker = reranker;
        _pool = pool;
    }

    public IReadOnlyList<RetrievedDocument> Chunks { get; }

    /// <summary>Keyword index alone.</summary>
    public IRetriever Keywords { get; }

    /// <summary>Vector index alone.</summary>
    public IRetriever Vectors { get; }

    /// <summary>Both indexes fused by reciprocal rank, before reranking.</summary>
    public IRetriever Hybrid { get; }

    /// <summary>Builds both indexes. The default reranker is a cross-encoder reranker with a local scorer.</summary>
    public static async Task<KnowledgeBase> BuildAsync(
        string directory,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        ResearchOptions options,
        IReranker? reranker = null,
        CancellationToken ct = default)
    {
        var chunks = await LoadChunksAsync(directory, options, ct);
        var vectors = (await embedder.GenerateAsync(chunks.Select(c => c.Text), cancellationToken: ct))
            .Select(e => e.Vector).ToArray();

        var store = new InMemoryVectorStore();
        await store.UpsertAsync(chunks, vectors, ct);
        var embeddingRetriever = new EmbeddingRetriever(embedder, store);

        var bm25 = new BM25Retriever(new BM25IndexOptions());
        await bm25.IndexAsync(chunks, ct);

        var hybrid = new HybridRetriever(
            [bm25, embeddingRetriever],
            new ReciprocalRankFusionReranker(),
            new HybridRetrieverOptions { TopKPerSource = options.CandidatePool });

        return new KnowledgeBase(
            chunks, bm25, embeddingRetriever, hybrid, reranker ?? new CrossEncoderReranker(new OverlapScorer()), options.CandidatePool);
    }

    /// <summary>Returns the best <paramref name="topK"/> chunks for the query, best first.</summary>
    public async Task<IReadOnlyList<RetrievedDocument>> SearchAsync(string query, int topK, CancellationToken ct = default)
    {
        var pool = await Hybrid.RetrieveAsync(query, _pool, ct: ct);
        return await _reranker.RerankAsync(query, pool, topK, ct);
    }

    static async Task<IReadOnlyList<RetrievedDocument>> LoadChunksAsync(
        string directory, ResearchOptions options, CancellationToken ct)
    {
        var loader = new MarkdownLoader();
        var splitter = new RecursiveCharacterSplitter();
        var chunks = new List<RetrievedDocument>();
        foreach (var path in Directory.GetFiles(directory, "*.md").OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            await using var stream = File.OpenRead(path);
            await foreach (var doc in loader.LoadAsync(stream, new DocumentLoadContext { SourceUri = name }, ct))
            {
                var title = ((IReadOnlyList<string>)doc.Metadata["headings"]!).FirstOrDefault() ?? name;
                var split = splitter.Split(
                    doc.Text,
                    new SplitterContext { ChunkSize = options.ChunkSize, ChunkOverlap = options.ChunkOverlap, SourceDocumentId = name });
                var n = 0;
                foreach (var chunk in split)
                {
                    chunks.Add(new RetrievedDocument(
                        $"{name}#{n++}",
                        chunk.Text.Trim(),
                        0,
                        new Dictionary<string, object?> { ["source"] = name, ["title"] = title }));
                }
            }
        }
        return chunks;
    }

    public void Dispose()
    {
        _bm25.Dispose();
        (_reranker as IDisposable)?.Dispose();
    }
}

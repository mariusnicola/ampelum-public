using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Retrieval.Loaders;
using SignalGrid.Ampelum.Retrieval.Persistence;
using SignalGrid.Ampelum.Retrieval.Splitters;

namespace Ampelum.Samples.ComposedPrograms.Style;

/// <summary>
/// The searchable memory of how release notes are written: the style guide page and the notes of past
/// releases, split into chunks, embedded and held in memory. The index can be saved to and restored
/// from a file so a restart does not embed everything again.
/// </summary>
public sealed class StyleIndex
{
    public const string StyleSource = "style";
    public const string HistorySource = "history";
    public const string StyleGuideFile = "style-guide.html";
    public const string PastReleasesFile = "past-releases.md";

    StyleIndex(InMemoryVectorStore store, IReadOnlyList<RetrievedDocument> documents)
    {
        Store = store;
        Documents = documents;
    }

    public InMemoryVectorStore Store { get; }

    public IReadOnlyList<RetrievedDocument> Documents { get; }

    /// <summary>Loads and splits both sources, embeds the chunks and fills a store.</summary>
    public static async Task<StyleIndex> BuildAsync(
        string dataDirectory, IEmbeddingGenerator<string, Embedding<float>> embedder, int chunkChars, CancellationToken ct)
    {
        var documents = new List<RetrievedDocument>();
        await foreach (var page in LoadAsync(new HtmlLoader(), Path.Combine(dataDirectory, StyleGuideFile), ct))
        {
            var chunks = new SentenceSplitter().Split(page.Text, new SplitterContext
            {
                ChunkSize = chunkChars,
                ChunkOverlap = 0,
                SourceDocumentId = page.Id,
            });
            documents.AddRange(chunks.Select((chunk, i) => Chunk($"{StyleSource}:{i}", chunk.Text, StyleSource)));
        }
        await foreach (var page in LoadAsync(new MarkdownLoader(), Path.Combine(dataDirectory, PastReleasesFile), ct))
        {
            var chunks = new MarkdownAwareSplitter().Split(page.Text, new SplitterContext
            {
                ChunkSize = chunkChars * 3,
                ChunkOverlap = 0,
                SourceDocumentId = page.Id,
            });
            // A chunk that is only a heading carries no wording to learn from.
            var perSection = new Dictionary<string, int>();
            documents.AddRange(chunks
                .Where(chunk => chunk.Text.Split('\n').Any(line => line.StartsWith("- ", StringComparison.Ordinal)))
                .Select(chunk =>
                {
                    var section = $"{HistorySource}:{chunk.Metadata["heading_path"]}";
                    // The first chunk of a section keeps the plain id; a section split in several gets a number.
                    var n = perSection.GetValueOrDefault(section) + 1;
                    perSection[section] = n;
                    return Chunk(n == 1 ? section : $"{section}#{n}", chunk.Text, HistorySource);
                }));
        }

        var vectors = await embedder.GenerateAsync(documents.Select(d => d.Text), cancellationToken: ct).ConfigureAwait(false);
        var store = new InMemoryVectorStore();
        await store.UpsertAsync(documents, vectors.Select(v => v.Vector).ToArray(), ct).ConfigureAwait(false);
        return new StyleIndex(store, documents);
    }

    public async Task SaveAsync(string path, CancellationToken ct)
    {
        await using var file = File.Create(path);
        await VectorIndexSerializer.SaveAsync(Store, file, ct).ConfigureAwait(false);
    }

    /// <summary>Restores a saved index. The documents list is rebuilt from the file by the caller if needed.</summary>
    public static async Task<StyleIndex> LoadAsync(string path, CancellationToken ct)
    {
        var store = new InMemoryVectorStore();
        await using var file = File.OpenRead(path);
        await VectorIndexSerializer.LoadAsync(store, file, ct).ConfigureAwait(false);
        return new StyleIndex(store, []);
    }

    static async IAsyncEnumerable<Document> LoadAsync(IDocumentLoader loader, string path,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        await foreach (var document in loader.LoadAsync(stream, new DocumentLoadContext { SourceUri = path }, ct).ConfigureAwait(false))
            yield return document;
    }

    static RetrievedDocument Chunk(string id, string text, string source) =>
        new(id, text.Trim(), 0, new Dictionary<string, object?> { ["source"] = source });
}

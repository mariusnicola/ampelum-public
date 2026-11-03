using Ampelum.Samples.ComposedPrograms.Drafting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Retrieval.Caching;
using SignalGrid.Ampelum.Retrieval.Evaluation;
using SignalGrid.Ampelum.Retrieval.Splitters;

namespace Ampelum.Samples.ComposedPrograms.Style;

/// <summary>
/// Gives each draft the style rules and the past notes closest to the change being described.
/// The notes are cut to a token budget so a long index cannot grow the prompt.
/// </summary>
public sealed class StyleMemory : IStyleNotes
{
    readonly IRetriever _retriever;
    readonly ReleaseNotesOptions _options;

    StyleMemory(IRetriever retriever, StyleIndex index, ReleaseNotesOptions options)
    {
        _retriever = retriever;
        Index = index;
        _options = options;
    }

    public StyleIndex Index { get; }

    public IRetriever Retriever => _retriever;

    /// <summary>Wraps an embedder so the same text is embedded once per cache.</summary>
    public static IEmbeddingGenerator<string, Embedding<float>> Cached(
        IEmbeddingGenerator<string, Embedding<float>> inner, IDistributedCache cache) =>
        new EmbeddingCache(inner, cache, new EmbeddingCacheOptions());

    /// <summary>Builds the index, or restores it from the configured file when that file exists.</summary>
    public static async Task<StyleMemory> OpenAsync(
        string dataDirectory, IChatClient chat, IEmbeddingGenerator<string, Embedding<float>> embedder,
        ReleaseNotesOptions options, CancellationToken ct = default)
    {
        StyleIndex index;
        if (options.StyleIndexPath is { } path && File.Exists(path))
        {
            index = await StyleIndex.LoadAsync(path, ct).ConfigureAwait(false);
        }
        else
        {
            index = await StyleIndex.BuildAsync(dataDirectory, embedder, options.StyleChunkChars, ct).ConfigureAwait(false);
            if (options.StyleIndexPath is { } target)
                await index.SaveAsync(target, ct).ConfigureAwait(false);
        }
        var retriever = StyleRetrieval.Create(options.StyleRetrieval, chat, embedder, index.Store, options.StyleMinScore);
        return new StyleMemory(retriever, index, options);
    }

    public async Task<string> NotesForAsync(string title, string details, CancellationToken ct)
    {
        var hits = await _retriever.RetrieveAsync($"{title}. {details}", _options.StyleTopK, ct: ct).ConfigureAwait(false);
        var notes = string.Join("\n", hits.Select(h => h.Text.Replace('\n', ' ').Trim()));
        return WithinTokenBudget(notes, _options.StyleTokenBudget);
    }

    /// <summary>Keeps the first tokens of the text, cut at a token boundary.</summary>
    internal static string WithinTokenBudget(string text, int tokens)
    {
        var first = new TokenSplitter().Split(text, new SplitterContext { ChunkSize = tokens, ChunkOverlap = 0 }).FirstOrDefault();
        return first?.Text.Trim() ?? "";
    }

    /// <summary>Mean rank metrics of the retriever over labeled queries in a CSV with query and gold_ids columns.</summary>
    public async Task<RetrievalEvalResult> EvaluateAsync(string csvPath, int topK, CancellationToken ct = default)
    {
        var harness = new RetrievalEvalHarness(_retriever, [new MRR(), new RecallAtK(topK), new PrecisionAtK(topK)], topK, 1);
        return await harness.RunAsync(SignalGrid.Ampelum.Evaluation.CsvDataset.FromFile(csvPath), ct).ConfigureAwait(false);
    }
}

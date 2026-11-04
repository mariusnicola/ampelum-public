using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Retrieval.Filtering;
using SignalGrid.Ampelum.Retrieval.Patterns;

namespace Ampelum.Samples.ComposedPrograms.Style;

/// <summary>How a draft's query is turned into searches over the style index.</summary>
public enum RetrievalMode { Direct, Rewrite, Hyde, MultiQuery, StepBack, SubQuestion, Routed }

/// <summary>Builds the retriever for a mode. Every mode ends in the same score floor.</summary>
public static class StyleRetrieval
{
    public const string RewritePrompt = "Rewrite the change as one past tense release note sentence. Reply with the sentence only.";
    public const string RouterPrompt = "Reply with one word: style for questions about wording rules, history for questions about earlier notes, default otherwise.";
    public const int Paraphrases = 3;
    public const int SubQuestions = 2;

    public static IRetriever Create(
        RetrievalMode mode, IChatClient chat, IEmbeddingGenerator<string, Embedding<float>> embedder,
        IVectorStore store, double minScore)
    {
        IRetriever Floor(IRetriever inner) => new ScoreThresholdRetriever(inner, minScore);
        var all = Floor(new EmbeddingRetriever(embedder, store));
        return mode switch
        {
            RetrievalMode.Direct => all,
            RetrievalMode.Rewrite => new RewritingRetriever(all, new QueryRewriter(chat, RewritePrompt)),
            RetrievalMode.Hyde => new HydeRetriever(all, chat, new HydeOptions()),
            RetrievalMode.MultiQuery => new MultiQueryRetriever(all, chat, new MultiQueryOptions { NumParaphrases = Paraphrases }),
            RetrievalMode.StepBack => new StepBackRetriever(all, chat, new StepBackOptions()),
            RetrievalMode.SubQuestion => new SubQuestionRetriever(all, chat, new SubQuestionOptions { MaxSubQuestions = SubQuestions }),
            RetrievalMode.Routed => new QueryRouter(chat, new QueryRouterOptions
            {
                SystemPrompt = RouterPrompt,
                Routes = new Dictionary<string, IRetriever>
                {
                    [StyleIndex.StyleSource] = new FilteredRetriever(all, FilterPredicate.Equals("source", StyleIndex.StyleSource)),
                    [StyleIndex.HistorySource] = new FilteredRetriever(all, FilterPredicate.Equals("source", StyleIndex.HistorySource)),
                    ["default"] = all,
                },
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown retrieval mode."),
        };
    }
}

/// <summary>
/// Restricts a retriever to documents whose metadata matches a predicate. The check runs on the returned
/// documents, over-fetching so enough survive it; a restored index matches a
/// store-side equality filter the same as before the save.
/// </summary>
public sealed class FilteredRetriever(IRetriever inner, FilterPredicate predicate, int candidateFactor = 4) : IRetriever
{
    public async Task<IReadOnlyList<RetrievedDocument>> RetrieveAsync(
        string query, int topK, IReadOnlyDictionary<string, object?>? filters = null, CancellationToken ct = default)
    {
        var candidates = await inner.RetrieveAsync(query, topK * candidateFactor, filters, ct).ConfigureAwait(false);
        return candidates.Where(d => predicate.Matches(d.Metadata)).Take(topK).ToList();
    }
}

/// <summary>Rewrites the query with a model, then searches with the rewrite.</summary>
public sealed class RewritingRetriever(IRetriever inner, QueryRewriter rewriter) : IRetriever
{
    public async Task<IReadOnlyList<RetrievedDocument>> RetrieveAsync(
        string query, int topK, IReadOnlyDictionary<string, object?>? filters = null, CancellationToken ct = default) =>
        await inner.RetrieveAsync(await rewriter.RewriteAsync(query, ct).ConfigureAwait(false), topK, filters, ct).ConfigureAwait(false);
}

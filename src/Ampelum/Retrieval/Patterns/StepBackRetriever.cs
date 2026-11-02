using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.Retrieval.Hybrid;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Retrieval.Patterns;

/// <summary>Step-back prompting retriever: abstracts the query then retrieves over both original and abstracted forms.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class StepBackRetriever(IRetriever inner, IChatClient chat, StepBackOptions options) : IRetriever
{
    private readonly QueryRewriter _rewriter = new(chat, options.SystemPrompt);

    /// <inheritdoc />
    public async Task<IReadOnlyList<RetrievedDocument>> RetrieveAsync(
        string query,
        int topK,
        IReadOnlyDictionary<string, object?>? filters = null,
        CancellationToken ct = default)
    {
        var stepBack = await _rewriter.RewriteAsync(query, ct).ConfigureAwait(false);
        var queries = options.IncludeOriginal ? new[] { query, stepBack } : new[] { stepBack };
        var perQ = await Task.WhenAll(queries.Select(q => inner.RetrieveAsync(q, topK, filters, ct))).ConfigureAwait(false);
        return ReciprocalRankFusionReranker.Fuse(perQ, k: 60, topK);
    }
}

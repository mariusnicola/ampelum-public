using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Retrieval.Hybrid;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Retrieval.Patterns;

/// <summary>Generates N paraphrases of a query, fans out to inner retriever, fuses results via RRF.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class MultiQueryRetriever(IRetriever inner, IChatClient chat, MultiQueryOptions options) : IRetriever
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<RetrievedDocument>> RetrieveAsync(
        string query,
        int topK,
        IReadOnlyDictionary<string, object?>? filters = null,
        CancellationToken ct = default)
    {
        using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.retriever.multiquery");
        var prompt = options.SystemPrompt.Replace("{N}", options.NumParaphrases.ToString());
        var messages = new[] { new ChatMessage(ChatRole.System, prompt), new ChatMessage(ChatRole.User, query) };
        var ambient = AmpelumContext.Ambient;
        var resp = ambient is null
            ? await chat.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false)
            : await HookedLmCall.InvokeAsync(ambient with { Chat = chat }, "MultiQueryRetriever.Paraphrase", messages, cancellationToken: ct)
                .ConfigureAwait(false);
        var paraphrases = resp.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(options.NumParaphrases).Prepend(query).Distinct().ToList();

        var perQ = await Task.WhenAll(paraphrases.Select(q => inner.RetrieveAsync(q, topK, filters, ct))).ConfigureAwait(false);
        return ReciprocalRankFusionReranker.Fuse(perQ, k: 60, topK);
    }
}

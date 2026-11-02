using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Hooks;

namespace SignalGrid.Ampelum.Retrieval.Patterns;

/// <summary>Single-shot LM query rewriter (returns rewritten string).</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class QueryRewriter(IChatClient chat, string systemPrompt)
{
    /// <summary>Sends the query through the LM with the configured system prompt; returns trimmed output.</summary>
    public async Task<string> RewriteAsync(string query, CancellationToken ct = default)
    {
        using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.retriever.rewrite");
        var messages = new[] { new ChatMessage(ChatRole.System, systemPrompt), new ChatMessage(ChatRole.User, query) };
        var ambient = AmpelumContext.Ambient;
        var resp = ambient is null
            ? await chat.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false)
            : await HookedLmCall.InvokeAsync(ambient with { Chat = chat }, "QueryRewriter.Rewrite", messages, cancellationToken: ct)
                .ConfigureAwait(false);
        return resp.Text.Trim();
    }
}

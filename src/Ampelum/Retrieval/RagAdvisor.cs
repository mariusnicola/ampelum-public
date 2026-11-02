using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Hooks;

namespace SignalGrid.Ampelum.Retrieval;

/// <summary>Sugar API: retrieve top-K, format as context, ask LM, return answer + citations.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class RagAdvisor(IRetriever retriever, IChatClient chat, RagAdvisorOptions options)
{
    /// <summary>Retrieves context, asks the chat model, returns the answer plus the cited documents.</summary>
    /// <remarks>
    /// <see cref="RagAdvisor"/> holds a raw <see cref="IChatClient"/>, not an <see cref="AmpelumContext"/> —
    /// unlike <c>Predict&lt;TSig&gt;</c>, it has no constructor-injected context to fall back to. When an
    /// <see cref="AmpelumContext.Ambient"/> scope is active, the call runs through
    /// <see cref="HookedLmCall.InvokeAsync"/> (using the constructor-supplied <c>chat</c> client as the
    /// effective client, so the hook pipeline runs but the caller's explicit client is still the one that
    /// answers) so the standard
    /// hook pipeline (budget/audit/PII/etc.) applies. With no ambient context — the common case for this
    /// sugar API, including all pre-existing call sites and tests — it falls back to the original direct
    /// <see cref="IChatClient.GetResponseAsync"/> call, unchanged, rather than throwing. This preserves
    /// backward compatibility for callers of this API that never push an ambient context.
    /// </remarks>
    public async Task<RagAdvisorResponse> AskAsync(string question, CancellationToken ct = default)
    {
        using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.rag.advisor");
        var docs = await retriever.RetrieveAsync(question, options.TopK, ct: ct).ConfigureAwait(false);
        var context = string.Join(
            options.ContextSeparator,
            docs.Select((d, i) => $"[doc{i + 1} id={d.Id}] {d.Text}"));
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "Answer the user using ONLY the context. Cite as [docN]."),
            new(ChatRole.User, $"Context:\n{context}\n\nQuestion: {question}"),
        };

        ChatResponse resp;
        if (AmpelumContext.Ambient is { } ambient)
        {
            var callContext = ReferenceEquals(chat, ambient.Chat) ? ambient : ambient with { Chat = chat };
            resp = await HookedLmCall.InvokeAsync(callContext, "RagAdvisor.Ask", messages, cancellationToken: ct).ConfigureAwait(false);
        }
        else
        {
            resp = await chat.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
        }

        return new RagAdvisorResponse(resp.Text, docs);
    }
}

/// <summary>Result of <see cref="RagAdvisor.AskAsync"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record RagAdvisorResponse(string Answer, IReadOnlyList<RetrievedDocument> Citations);

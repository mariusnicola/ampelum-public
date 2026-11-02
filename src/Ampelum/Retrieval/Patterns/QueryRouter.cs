using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Hooks;

namespace SignalGrid.Ampelum.Retrieval.Patterns;

/// <summary>LM-driven dispatcher that picks one of several inner retrievers per query.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class QueryRouter(IChatClient chat, QueryRouterOptions options) : IRetriever
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<RetrievedDocument>> RetrieveAsync(
        string query,
        int topK,
        IReadOnlyDictionary<string, object?>? filters = null,
        CancellationToken ct = default)
    {
        using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.retriever.router");
        var hookCtx = AmpelumContext.Ambient is { } ambient
            ? ambient with { Chat = chat }
            : new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        var resp = await HookedLmCall.InvokeAsync(
            hookCtx,
            "QueryRouter.Route",
            [new ChatMessage(ChatRole.System, options.SystemPrompt), new ChatMessage(ChatRole.User, query)],
            cancellationToken: ct).ConfigureAwait(false);
        var route = resp.Text.Trim();
        IRetriever target;
        if (options.Routes.TryGetValue(route, out var r))
        {
            target = r;
        }
        else if (options.Routes.TryGetValue(options.DefaultRoute, out var d))
        {
            target = d;
        }
        else
        {
            target = options.Routes.Values.First();
        }
        activity?.SetTag("SignalGrid.Ampelum.router.route", route);
        return await target.RetrieveAsync(query, topK, filters, ct).ConfigureAwait(false);
    }
}

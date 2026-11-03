using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Caching;

namespace Ampelum.Samples.ChatFrontend;

/// <summary>
/// Answers a repeated identical prompt from <see cref="TokenCache"/>. Nothing in the library calls
/// TokenCache, so this decorator is the wiring. Streaming calls pass through uncached.
/// </summary>
public sealed class TokenCachedChatClient(IChatClient inner, TokenCache cache) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var sent = messages as IList<ChatMessage> ?? [.. messages];
        var key = TokenCache.BuildKey(sent, options);
        if (cache.TryGet(key) is { } hit)
            return hit;
        var response = await base.GetResponseAsync(sent, options, cancellationToken).ConfigureAwait(false);
        cache.Set(key, response);
        return response;
    }
}

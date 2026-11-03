using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Caching;

/// <summary>
/// Answers a repeated identical request from a <see cref="TokenCache"/> instead of calling the wrapped client.
/// The key is <see cref="TokenCache.BuildKey"/>: the messages plus the options that change the output. Wrap the
/// client whose model id the options carry; a decorator placed outside one that sets the model id later
/// keys on an empty model id. Streaming calls pass through uncached.
/// </summary>
public sealed class TokenCachedChatClient(IChatClient inner, TokenCache cache) : DelegatingChatClient(inner)
{
    readonly TokenCache _cache = cache ?? throw new ArgumentNullException(nameof(cache));

    /// <inheritdoc/>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var sent = messages as IList<ChatMessage> ?? [.. messages];
        var key = TokenCache.BuildKey(sent, options);
        if (_cache.TryGet(key) is { } hit)
            return hit;
        var response = await base.GetResponseAsync(sent, options, cancellationToken).ConfigureAwait(false);
        _cache.Set(key, response);
        return response;
    }
}

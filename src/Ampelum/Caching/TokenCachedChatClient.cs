using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Caching;

/// <summary>
/// Answers a repeated identical request from a <see cref="TokenCache"/> instead of calling the wrapped client.
/// The key is <see cref="TokenCache.BuildKey"/>: the whole request. A request the key cannot represent
/// (see <see cref="TokenCache.IsCacheable"/>) passes through uncached. A hit is a copy of the stored response
/// with zero token usage. Wrap the
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
        var key = TryBuildKey(sent, options);
        if (key is null)
            return await base.GetResponseAsync(sent, options, cancellationToken).ConfigureAwait(false);

        if (_cache.TryGet(key) is { } hit)
            return Copy(hit, zeroUsage: true)!;
        var response = await base.GetResponseAsync(sent, options, cancellationToken).ConfigureAwait(false);
        // A response that cannot be copied cannot be cached safely: it is returned and not stored.
        if (Copy(response, zeroUsage: false) is { } stored)
            _cache.Set(key, stored);
        return response;
    }

    // A request the key cannot represent is not cached.
    static string? TryBuildKey(IList<ChatMessage> messages, ChatOptions? options)
    {
        if (!TokenCache.IsCacheable(options))
            return null;
        // Any serializer failure (unsupported type, a getter that throws) means the request cannot be keyed: pass it through.
        try { return TokenCache.BuildKey(messages, options); }
        catch (Exception)
        {
            return null;
        }
    }

    // A deep copy through the response's own JSON form, so the caller and the cache share no content item, usage or
    // dictionary; a hit costs no tokens. Null when the response cannot be serialized.
    static ChatResponse? Copy(ChatResponse source, bool zeroUsage)
    {
        try
        {
            var copy = System.Text.Json.JsonSerializer.Deserialize<ChatResponse>(
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(source, AIJsonUtilities.DefaultOptions),
                AIJsonUtilities.DefaultOptions)!;
            if (zeroUsage)
                copy.Usage = new UsageDetails { InputTokenCount = 0, OutputTokenCount = 0, TotalTokenCount = 0 };
            return copy;
        }
        catch (Exception)
        {
            // The model call already succeeded; a response the serializer cannot copy is returned uncached.
            return null;
        }
    }
}

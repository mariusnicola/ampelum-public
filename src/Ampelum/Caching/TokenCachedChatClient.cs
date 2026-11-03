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
            return Copy(hit, zeroUsage: true);
        var response = await base.GetResponseAsync(sent, options, cancellationToken).ConfigureAwait(false);
        _cache.Set(key, Copy(response, zeroUsage: false));
        return response;
    }

    // A request the key cannot represent is not cached.
    static string? TryBuildKey(IList<ChatMessage> messages, ChatOptions? options)
    {
        if (!TokenCache.IsCacheable(options))
            return null;
        try { return TokenCache.BuildKey(messages, options); }
        catch (Exception ex) when (ex is NotSupportedException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // ChatMessage.Clone is shallow: the content list would still be shared.
    static ChatMessage CopyMessage(ChatMessage m) =>
        new(m.Role, [.. m.Contents])
        {
            AuthorName = m.AuthorName,
            CreatedAt = m.CreatedAt,
            MessageId = m.MessageId,
            AdditionalProperties = m.AdditionalProperties is null ? null : new AdditionalPropertiesDictionary(m.AdditionalProperties),
        };

    // The caller and the cache never share an instance, and a hit costs no tokens.
    static ChatResponse Copy(ChatResponse source, bool zeroUsage) =>
        new([.. source.Messages.Select(CopyMessage)])
        {
            ResponseId = source.ResponseId,
            ConversationId = source.ConversationId,
            ModelId = source.ModelId,
            CreatedAt = source.CreatedAt,
            FinishReason = source.FinishReason,
            Usage = zeroUsage
                ? new UsageDetails { InputTokenCount = 0, OutputTokenCount = 0, TotalTokenCount = 0 }
                : source.Usage,
            AdditionalProperties = source.AdditionalProperties is null ? null : new AdditionalPropertiesDictionary(source.AdditionalProperties),
        };
}

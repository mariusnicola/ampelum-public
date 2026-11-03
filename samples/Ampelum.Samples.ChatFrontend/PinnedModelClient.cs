using Microsoft.Extensions.AI;

namespace Ampelum.Samples.ChatFrontend;

/// <summary>
/// Sends every call with one model id. The router picks an endpoint, not a model, so each endpoint
/// carries its own id; this is also what lets two endpoints share one provider client.
/// </summary>
public sealed class PinnedModelClient(IChatClient inner, string modelId) : DelegatingChatClient(inner)
{
    public string ModelId { get; } = modelId;

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(messages, Pin(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(messages, Pin(options), cancellationToken);

    ChatOptions Pin(ChatOptions? options)
    {
        var pinned = options?.Clone() ?? new ChatOptions();
        pinned.ModelId = ModelId;
        return pinned;
    }
}

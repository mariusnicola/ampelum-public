using Microsoft.Extensions.AI;

namespace Ampelum.ComparativeBenchmarks.Competitors.Shared;

/// <summary>
/// Byte-identical canned <see cref="IChatClient"/> shared across every competitor.
/// Returns the same string for every call so we measure orchestration overhead,
/// not LM latency variance. Streaming yields one chunk after a single Task.Yield.
/// </summary>
public sealed class MockChatClient : IChatClient
{
    private readonly string _canned;
    private readonly ChatResponse _cachedResponse;

    public MockChatClient(string canned)
    {
        _canned = canned;
        _cachedResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, canned));
    }

    public ChatClientMetadata Metadata { get; } = new("mock", new Uri("inproc://mock"));

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default)
        => Task.FromResult(_cachedResponse);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default)
        => YieldOne(_canned);

    private static async IAsyncEnumerable<ChatResponseUpdate> YieldOne(string s)
    {
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, s);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

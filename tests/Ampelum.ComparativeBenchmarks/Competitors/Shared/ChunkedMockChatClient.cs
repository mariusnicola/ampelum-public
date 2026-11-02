using Microsoft.Extensions.AI;

namespace Ampelum.ComparativeBenchmarks.Competitors.Shared;

/// <summary>
/// Streaming-focused canned <see cref="IChatClient"/>. Yields a configurable
/// number of <see cref="ChatResponseUpdate"/> chunks containing the same
/// pre-formatted ChatAdapter envelope so each scenario row processes a
/// byte-identical stream and the delta is pure parser/orchestration overhead.
/// </summary>
public sealed class ChunkedMockChatClient : IChatClient
{
    private readonly string[] _chunks;
    private readonly string _concatenated;
    private readonly ChatResponse _cachedResponse;

    /// <summary>Splits <paramref name="canned"/> into <paramref name="chunkCount"/> roughly-equal chunks.</summary>
    public ChunkedMockChatClient(string canned, int chunkCount)
    {
        _chunks = SplitEqual(canned, chunkCount);
        _concatenated = canned;
        _cachedResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, canned));
    }

    public ChatClientMetadata Metadata { get; } = new("chunked-mock", new Uri("inproc://chunked-mock"));

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default)
        => Task.FromResult(_cachedResponse);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default)
        => YieldChunks(_chunks, ct);

    private static async IAsyncEnumerable<ChatResponseUpdate> YieldChunks(
        string[] chunks,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // Single Task.Yield up front keeps the path async-warm without per-chunk
        // suspension overhead — matches what a real provider's stream looks like
        // once the TCP connection is hot.
        await Task.Yield();
        for (int i = 0; i < chunks.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunks[i]);
        }
    }

    private static string[] SplitEqual(string s, int count)
    {
        if (count <= 1) return [s];
        var result = new string[count];
        int len = s.Length;
        int chunk = Math.Max(1, len / count);
        int idx = 0;
        for (int i = 0; i < count; i++)
        {
            if (i == count - 1) { result[i] = s.Substring(idx); }
            else
            {
                int take = Math.Min(chunk, len - idx);
                result[i] = s.Substring(idx, Math.Max(0, take));
                idx += take;
            }
        }
        return result;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

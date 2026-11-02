using Microsoft.Extensions.AI;

namespace Ampelum.ComparativeBenchmarks.Competitors.Shared;

/// <summary>
/// Variant of <see cref="MockChatClient"/> that forces a real async suspension
/// point via <see cref="Task.Yield"/> before returning the canned response.
///
/// <para>
/// Used by <see cref="Scenarios.Scenario09_NParallelScaling"/>. The plain
/// <see cref="MockChatClient"/> returns <see cref="Task.FromResult{T}"/>, which
/// completes synchronously; on a parallel-fan-out benchmark
/// (<c>for i: tasks[i] = ForwardAsync(); WhenAll(tasks);</c>) every continuation
/// then runs synchronously on the calling thread, serialising N "parallel"
/// tasks into a sequential loop and producing artifactual N× wall-clock scaling
/// that is unrelated to product-code contention.
/// </para>
///
/// <para>
/// <see cref="Task.Yield"/> hops to a threadpool worker, distributing the
/// concurrent forwards as the benchmark name implies. The remaining
/// per-N inflation (vs the <c>EmptyAsync</c> probe in the same scenario)
/// reflects threadpool-dispatch + WhenAll fan-in overhead, which is the
/// honest physical floor for sub-microsecond per-task workloads.
/// </para>
/// </summary>
public sealed class YieldingMockChatClient : IChatClient
{
    private readonly string _canned;
    private readonly ChatResponse _cachedResponse;

    public YieldingMockChatClient(string canned)
    {
        _canned = canned;
        _cachedResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, canned));
    }

    public ChatClientMetadata Metadata { get; } = new("mock", new Uri("inproc://mock-yield"));

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default)
    {
        await Task.Yield();
        return _cachedResponse;
    }

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

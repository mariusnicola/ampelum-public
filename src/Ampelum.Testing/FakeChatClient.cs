using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Testing;

/// <summary>
/// An in-memory <see cref="IChatClient"/> for unit testing Ampelum modules without a live LM endpoint.
/// Callers enqueue responses (or errors) before running a forward pass; the client dequeues one entry per call.
/// </summary>
public sealed class FakeChatClient : IChatClient
{
    readonly ConcurrentQueue<object> _queue = new();
    readonly ConcurrentQueue<string[]> _streamingQueue = new();
    readonly List<RecordedChatCall> _calls = [];
    readonly object _lock = new();

    /// <inheritdoc/>
    public ChatClientMetadata Metadata { get; } = new(nameof(FakeChatClient));

    /// <summary>Enqueues a pre-built <see cref="ChatResponse"/> to be returned by the next call.</summary>
    /// <param name="response">The response to return.</param>
    /// <returns>This instance for fluent chaining.</returns>
    public FakeChatClient EnqueueRaw(ChatResponse response)
    {
        _queue.Enqueue(response);
        return this;
    }

    /// <summary>Enqueues a plain-text assistant response to be returned by the next call.</summary>
    /// <param name="text">The assistant message text.</param>
    /// <returns>This instance for fluent chaining.</returns>
    public FakeChatClient EnqueueText(string text) =>
        EnqueueRaw(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

    /// <summary>Enqueues a plain-text assistant response with optional usage details to be returned by the next call.</summary>
    /// <param name="text">The assistant message text.</param>
    /// <param name="usage">Optional <see cref="UsageDetails"/> to attach to the response.</param>
    /// <returns>This instance for fluent chaining.</returns>
    public FakeChatClient EnqueueText(string text, UsageDetails? usage)
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { Usage = usage };
        return EnqueueRaw(response);
    }

    /// <summary>
    /// Enqueues a plain-text assistant response with optional usage, model id, and response id —
    /// the inputs needed to verify OTel GenAI semconv 1.27.0 (<c>gen_ai.usage.*_tokens</c>,
    /// <c>gen_ai.response.model</c>, <c>gen_ai.response.id</c>) attribution on emitted spans.
    /// </summary>
    /// <param name="text">The assistant message text.</param>
    /// <param name="usage">Optional <see cref="UsageDetails"/> to attach.</param>
    /// <param name="modelId">Optional model id to populate <see cref="ChatResponse.ModelId"/>.</param>
    /// <param name="responseId">Optional response id to populate <see cref="ChatResponse.ResponseId"/>.</param>
    /// <returns>This instance for fluent chaining.</returns>
    public FakeChatClient EnqueueText(string text, UsageDetails? usage, string? modelId, string? responseId)
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            Usage = usage,
            ModelId = modelId,
            ResponseId = responseId,
        };
        return EnqueueRaw(response);
    }

    /// <summary>Enqueues an exception to be thrown by the next call (simulates an LM failure).</summary>
    /// <param name="ex">The exception to throw.</param>
    /// <returns>This instance for fluent chaining.</returns>
    public FakeChatClient EnqueueError(Exception ex)
    {
        _queue.Enqueue(ex);
        return this;
    }

    /// <summary>
    /// Enqueues a type-safe response by serialising the signature's output fields through the adapter.
    /// Uses <see cref="ChatAdapter"/> by default; pass a custom adapter for <see cref="JsonAdapter"/> scenarios.
    /// </summary>
    /// <typeparam name="TSig">The signature type whose output properties are set on <paramref name="outputs"/>.</typeparam>
    /// <param name="outputs">A populated signature record with all output properties set.</param>
    /// <param name="adapter">Optional adapter to use for serialisation; defaults to <see cref="ChatAdapter"/>.</param>
    /// <returns>This instance for fluent chaining.</returns>
    public FakeChatClient EnqueueOutputs<TSig>(TSig outputs, IAdapter? adapter = null) where TSig : ISignature
    {
        ArgumentNullException.ThrowIfNull(outputs);
        var effective = adapter ?? new ChatAdapter();
        if (effective is not ITestableAdapter testable)
            throw new InvalidOperationException($"Adapter '{effective.GetType().Name}' does not implement ITestableAdapter.");
        var meta = SignatureMetadataCache.For<TSig>();
        var dict = new Dictionary<string, object?>(meta.Outputs.Count);
        foreach (var f in meta.Outputs)
            dict[f.Name] = outputs.GetType().GetProperty(f.Name)!.GetValue(outputs);
        return EnqueueRaw(testable.FormatResponse(meta, dict));
    }

    /// <summary>Returns a thread-safe snapshot of all calls made to <see cref="GetResponseAsync"/> so far.</summary>
    public IReadOnlyList<RecordedChatCall> RecordedCalls
    {
        get { lock (_lock) return _calls.ToArray(); }
    }

    /// <inheritdoc/>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        lock (_lock) _calls.Add(new RecordedChatCall(messages.ToArray(), options, cancellationToken));
        if (!_queue.TryDequeue(out var next))
            throw new InvalidOperationException("FakeChatClient: no enqueued response.");
        if (next is ChatResponse r) return Task.FromResult(r);
        return Task.FromException<ChatResponse>((Exception)next);
    }

    /// <summary>
    /// Enqueues an array of raw string chunks to be yielded one-per-chunk by the next
    /// <see cref="GetStreamingResponseAsync"/> call.
    /// </summary>
    /// <param name="chunks">The individual text chunks to stream.</param>
    /// <returns>This instance for fluent chaining.</returns>
    public FakeChatClient EnqueueStreamingChunks(params string[] chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        _streamingQueue.Enqueue(chunks);
        return this;
    }

    /// <summary>
    /// Serialises <paramref name="outputs"/> via the adapter's <c>FormatResponse</c>, splits the wire text into
    /// per-field chunks (marker + body), and enqueues those chunks for the next
    /// <see cref="GetStreamingResponseAsync"/> call.
    /// Uses <see cref="ChatAdapter"/> by default; pass a custom adapter for <see cref="JsonAdapter"/> scenarios.
    /// </summary>
    /// <typeparam name="TSig">The signature type whose output properties are set on <paramref name="outputs"/>.</typeparam>
    /// <param name="outputs">A populated signature record with all output properties set.</param>
    /// <param name="adapter">Optional adapter to use for serialisation; defaults to <see cref="ChatAdapter"/>.</param>
    /// <returns>This instance for fluent chaining.</returns>
    public FakeChatClient EnqueueStreamingOutputs<TSig>(TSig outputs, IAdapter? adapter = null) where TSig : ISignature
    {
        ArgumentNullException.ThrowIfNull(outputs);
        var effective = adapter ?? new ChatAdapter();
        if (effective is not ITestableAdapter testable)
            throw new InvalidOperationException($"Adapter '{effective.GetType().Name}' does not implement ITestableAdapter.");
        var meta = SignatureMetadataCache.For<TSig>();
        var dict = new Dictionary<string, object?>(meta.Outputs.Count);
        foreach (var f in meta.Outputs)
            dict[f.Name] = outputs.GetType().GetProperty(f.Name)!.GetValue(outputs);
        var wire = testable.FormatResponse(meta, dict).Text!;
        var chunks = new List<string>(meta.Outputs.Count);
        foreach (var f in meta.Outputs)
        {
            var marker = $"[[ ## {f.Name} ## ]]";
            var idx = wire.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) continue;
            var nextIdx = int.MaxValue;
            foreach (var g in meta.Outputs)
            {
                if (g.Name != f.Name)
                {
                    var ni = wire.IndexOf($"[[ ## {g.Name} ## ]]", idx + marker.Length, StringComparison.Ordinal);
                    if (ni >= 0) nextIdx = Math.Min(nextIdx, ni);
                }
            }
            var end = nextIdx == int.MaxValue ? wire.Length : nextIdx;
            chunks.Add(wire[idx..end]);
        }
        _streamingQueue.Enqueue(chunks.ToArray());
        return this;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        lock (_lock) _calls.Add(new RecordedChatCall(messages.ToArray(), options, cancellationToken));
        if (!_streamingQueue.TryDequeue(out var chunks))
            throw new InvalidOperationException("FakeChatClient: no enqueued streaming chunks.");
        foreach (var c in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, c);
        }
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc/>
    public void Dispose() { }
}

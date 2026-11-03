using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Testing;

namespace Ampelum.Samples.PromptTuning;

/// <summary>
/// A scripted <see cref="IChatClient"/> for the reflection model of SIMBA. Every call is a request for a rule
/// and gets <see cref="ScriptedChatClient.TunedInstruction"/>, the text the scripted classifiers reward.
/// </summary>
public sealed class ScriptedRuleClient : IChatClient
{
    readonly FakeChatClient _inner = new();
    readonly object _gate = new();

    /// <summary>Every call SIMBA made, in order.</summary>
    public IReadOnlyList<RecordedChatCall> RecordedCalls => _inner.RecordedCalls;

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _inner.EnqueueText(ScriptedChatClient.TunedInstruction);
            return _inner.GetResponseAsync(messages, options, cancellationToken);
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The scripted client does not stream.");

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc />
    public void Dispose() => _inner.Dispose();
}

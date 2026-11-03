using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Testing;

namespace Ampelum.Samples.PromptTuning;

/// <summary>
/// A scripted <see cref="IChatClient"/> for the instruction proposer of <see cref="PromptTuningPipeline"/>.
/// The first call is the dataset summary and gets <see cref="Summary"/>. Every later call is an
/// instruction proposal and gets the next entry of the proposal list, wrapping around at the end.
/// </summary>
public sealed class ScriptedProposerClient : IChatClient
{
    /// <summary>Reply to the dataset summary call.</summary>
    public const string Summary =
        "Short customer feedback about orders, delivery, support and an app, labeled positive, negative or neutral.";

    readonly FakeChatClient _inner = new();
    readonly IReadOnlyList<string> _proposals;
    readonly object _gate = new();
    int _calls;

    ScriptedProposerClient(IReadOnlyList<string> proposals) => _proposals = proposals;

    /// <summary>Creates the client. Without arguments it proposes the tuned and the weak instruction.</summary>
    /// <param name="proposals">Replies to the proposal calls, in order.</param>
    public static ScriptedProposerClient Create(params string[] proposals)
    {
        if (proposals.Length == 0)
            proposals = [ScriptedChatClient.TunedInstruction, ScriptedChatClient.WeakInstruction];
        return new ScriptedProposerClient(proposals);
    }

    /// <summary>Every call the optimizer made, in order.</summary>
    public IReadOnlyList<RecordedChatCall> RecordedCalls => _inner.RecordedCalls;

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var reply = _calls == 0 ? Summary : _proposals[(_calls - 1) % _proposals.Count];
            _calls++;
            _inner.EnqueueText(reply);
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

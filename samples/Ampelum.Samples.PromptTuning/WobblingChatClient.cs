using Microsoft.Extensions.AI;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Evaluation;
using SignalGrid.Ampelum.Testing;

namespace Ampelum.Samples.PromptTuning;

/// <summary>
/// A scripted classifier <see cref="IChatClient"/> that gives a different answer when the same program sees
/// the same row twice, as a sampling model does. Without help it answers a row wrong the first time and then
/// alternates right and wrong. With <see cref="ScriptedChatClient.TunedInstruction"/> in the system text, or
/// with demonstrations in the prompt, it answers right every time. SIMBA needs that run to run difference to
/// find rows worth learning from; a model that always answers the same gives it nothing. The scores come
/// from this script, not from any model.
/// </summary>
public sealed class WobblingChatClient : IChatClient
{
    readonly FakeChatClient _inner = new();
    readonly IReadOnlyList<LabeledExample<SentimentSig>> _data;
    readonly List<string> _labels;
    readonly Dictionary<int, int> _seen = [];
    readonly object _gate = new();

    WobblingChatClient(IReadOnlyList<LabeledExample<SentimentSig>> data)
    {
        _data = data;
        _labels = data.Select(e => e.Expected.Sentiment).Distinct().Order().ToList();
    }

    /// <summary>Creates the client.</summary>
    /// <param name="data">The labeled rows the sample classifies.</param>
    public static WobblingChatClient Create(IReadOnlyList<LabeledExample<SentimentSig>> data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new WobblingChatClient(data);
    }

    /// <summary>Every call the sample made, in order.</summary>
    public IReadOnlyList<RecordedChatCall> RecordedCalls => _inner.RecordedCalls;

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        lock (_gate)
        {
            _inner.EnqueueText(ReplyFor(list));
            return _inner.GetResponseAsync(list, options, cancellationToken);
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

    string ReplyFor(IReadOnlyList<ChatMessage> messages)
    {
        var user = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        var row = -1;
        for (var i = 0; i < _data.Count && row < 0; i++)
            if (user.Contains(_data[i].Inputs.Feedback, StringComparison.Ordinal))
                row = i;
        if (row < 0)
            throw new InvalidOperationException("The scripted client only answers dataset rows.");

        var system = messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Text ?? "";
        // The adapter sends one system message, then a user and an assistant message per demo, then the row.
        var steady = system.Contains(ScriptedChatClient.TunedInstruction, StringComparison.Ordinal)
                     || messages.Count > 2;
        var occurrence = _seen.GetValueOrDefault(row);
        _seen[row] = occurrence + 1;

        var right = _data[row].Expected.Sentiment;
        var label = steady || occurrence % 2 == 1 ? right : _labels.First(l => l != right);
        return new ChatAdapter().FormatResponse(
            SignatureMetadataCache.For<SentimentSig>(),
            new Dictionary<string, object?> { [nameof(SentimentSig.Sentiment)] = label }).Text;
    }
}

using Microsoft.Extensions.AI;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Testing;

namespace Ampelum.Samples.PromptTuning;

/// <summary>
/// A scripted classifier <see cref="IChatClient"/> for <see cref="PromptTuningPipeline"/>. Every call
/// is a classification of one dataset row. The reply is the right label for the first N rows and a
/// wrong label for the rest, where N depends on the instruction in the system text and on whether
/// the prompt carries demonstrations. The scores it produces come from this script, not from any model.
/// </summary>
public sealed class ScriptedChatClient : IChatClient
{
    /// <summary>Rows answered correctly with the signature's own instruction.</summary>
    public const int BaselineCorrect = 3;

    /// <summary>Rows answered correctly when <see cref="TunedInstruction"/> is in the system text.</summary>
    public const int TunedCorrect = 7;

    /// <summary>Rows answered correctly when <see cref="WeakInstruction"/> is in the system text.</summary>
    public const int WeakCorrect = 1;

    /// <summary>Extra rows answered correctly when the prompt carries at least one demonstration.</summary>
    public const int DemoBonus = 1;

    /// <summary>Instruction the scripted classifier rewards by default.</summary>
    public const string TunedInstruction =
        "Read the customer feedback and answer with exactly one label: positive, negative or neutral. " +
        "Judge the overall tone, and treat a complaint inside otherwise polite text as negative.";

    /// <summary>Instruction the scripted classifier punishes.</summary>
    public const string WeakInstruction = "Answer in one word.";

    readonly FakeChatClient _inner = new();
    readonly IReadOnlyList<LabeledExample<SentimentSig>> _data;
    readonly List<string> _labels;
    readonly string _rewarded;
    readonly int _rewardedCorrect;
    readonly int _baselineCorrect;
    readonly object _gate = new();

    ScriptedChatClient(
        IReadOnlyList<LabeledExample<SentimentSig>> data,
        string rewarded,
        int rewardedCorrect,
        int baselineCorrect)
    {
        _data = data;
        _rewarded = rewarded;
        _rewardedCorrect = rewardedCorrect;
        _baselineCorrect = baselineCorrect;
        _labels = data.Select(e => e.Expected.Sentiment).Distinct().Order().ToList();
    }

    /// <summary>Creates the client. Rows after the first N get a wrong label.</summary>
    /// <param name="data">The labeled rows the pipeline classifies.</param>
    /// <param name="rewarded">Instruction that earns <paramref name="rewardedCorrect"/> right answers.</param>
    /// <param name="rewardedCorrect">Right answers when <paramref name="rewarded"/> is in the system text.</param>
    /// <param name="baselineCorrect">Right answers when it is not.</param>
    public static ScriptedChatClient Create(
        IReadOnlyList<LabeledExample<SentimentSig>> data,
        string rewarded = TunedInstruction,
        int rewardedCorrect = TunedCorrect,
        int baselineCorrect = BaselineCorrect)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(rewarded);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rewardedCorrect, data.Count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(baselineCorrect, data.Count);
        return new ScriptedChatClient(data, rewarded, rewardedCorrect, baselineCorrect);
    }

    /// <summary>Every call the pipeline made, in order.</summary>
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
        var correct = system.Contains(_rewarded, StringComparison.Ordinal) ? _rewardedCorrect
            : system.Contains(WeakInstruction, StringComparison.Ordinal) ? WeakCorrect
            : _baselineCorrect;
        // The adapter sends one system message, then a user and an assistant message per demo, then the row.
        if (messages.Count > 2)
            correct = Math.Min(correct + DemoBonus, _data.Count);

        var right = _data[row].Expected.Sentiment;
        var label = row < correct ? right : _labels.First(l => l != right);
        return new ChatAdapter().FormatResponse(
            SignatureMetadataCache.For<SentimentSig>(),
            new Dictionary<string, object?> { [nameof(SentimentSig.Sentiment)] = label }).Text;
    }
}

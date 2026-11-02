using Microsoft.Extensions.AI;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Testing;

namespace Ampelum.Samples.PromptTuning;

/// <summary>
/// A scripted <see cref="IChatClient"/> for <see cref="PromptTuningPipeline"/>. Each reply is chosen
/// from the prompt it receives: a classification call gets the right label for the first
/// N dataset rows, where N depends on whether the proposed instruction is in the system text.
/// A call that is not about a dataset row is the instruction proposal and gets the proposed text.
/// The scores it produces come from this script, not from any model.
/// </summary>
public sealed class ScriptedChatClient : IChatClient
{
    /// <summary>Rows answered correctly when the proposed instruction is absent.</summary>
    public const int BaselineCorrect = 3;

    /// <summary>Rows answered correctly when the default proposed instruction is present.</summary>
    public const int TunedCorrect = 7;

    /// <summary>Instruction the scripted proposer returns by default.</summary>
    public const string TunedInstruction =
        "Read the customer feedback and answer with exactly one label: positive, negative or neutral. " +
        "Judge the overall tone, and treat a complaint inside otherwise polite text as negative.";

    readonly FakeChatClient _inner = new();
    readonly IReadOnlyList<LabeledExample<SentimentSig>> _data;
    readonly List<string> _labels;
    readonly string _proposal;
    readonly int _proposalCorrect;
    readonly int _baselineCorrect;
    readonly object _gate = new();

    ScriptedChatClient(
        IReadOnlyList<LabeledExample<SentimentSig>> data,
        string proposal,
        int proposalCorrect,
        int baselineCorrect)
    {
        _data = data;
        _proposal = proposal;
        _proposalCorrect = proposalCorrect;
        _baselineCorrect = baselineCorrect;
        _labels = data.Select(e => e.Expected.Sentiment).Distinct().Order().ToList();
    }

    /// <summary>Creates the client. Rows after the first N get a wrong label.</summary>
    public static ScriptedChatClient Create(
        IReadOnlyList<LabeledExample<SentimentSig>> data,
        string proposal = TunedInstruction,
        int proposalCorrect = TunedCorrect,
        int baselineCorrect = BaselineCorrect)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposal);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(proposalCorrect, data.Count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(baselineCorrect, data.Count);
        return new ScriptedChatClient(data, proposal, proposalCorrect, baselineCorrect);
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
            return _proposal;

        var system = messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Text ?? "";
        var correct = system.Contains(_proposal, StringComparison.Ordinal) ? _proposalCorrect : _baselineCorrect;
        var right = _data[row].Expected.Sentiment;
        var label = row < correct ? right : _labels.First(l => l != right);
        return new ChatAdapter().FormatResponse(
            SignatureMetadataCache.For<SentimentSig>(),
            new Dictionary<string, object?> { [nameof(SentimentSig.Sentiment)] = label }).Text;
    }
}

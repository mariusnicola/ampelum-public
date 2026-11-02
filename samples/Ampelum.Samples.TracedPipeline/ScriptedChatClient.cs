using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Testing;

namespace Ampelum.Samples.TracedPipeline;

/// <summary>
/// A scripted <see cref="IChatClient"/>. A classification call is answered correctly for the first N
/// dataset rows, where N is the base count for the instruction in the system text plus one for each
/// demo whose label is right. A proposal or rule request gets a fixed text. The scores it produces
/// come from this script, not from any model.
/// </summary>
public sealed partial class ScriptedChatClient : IChatClient
{
    /// <summary>Rows answered correctly with the untouched instruction and no demos.</summary>
    public const int BaselineCorrect = 3;

    /// <summary>First instruction the proposer returns; it scores poorly.</summary>
    public const string WeakProposal = "Answer with one word.";

    /// <summary>Second instruction the proposer returns; it scores best.</summary>
    public const string BestProposal =
        "Read the feedback and answer with exactly one label: positive, negative or neutral. " +
        "Treat a complaint inside otherwise polite text as negative.";

    /// <summary>Rules the rule proposer returns.</summary>
    public const string Rules = "Complaints about delay or silence are negative; plain facts are neutral.";

    /// <summary>Rows answered correctly when <see cref="WeakProposal"/> is the instruction.</summary>
    public const int WeakCorrect = 4;

    /// <summary>Rows answered correctly when <see cref="BestProposal"/> is the instruction.</summary>
    public const int BestCorrect = 8;

    /// <summary>Rows answered correctly when <see cref="Rules"/> is the instruction.</summary>
    public const int RulesCorrect = 6;

    readonly FakeChatClient _inner = new();
    readonly IReadOnlyList<LabeledExample<SentimentSig>> _data;
    readonly List<string> _labels;
    readonly object _gate = new();

    ScriptedChatClient(IReadOnlyList<LabeledExample<SentimentSig>> data)
    {
        _data = data;
        _labels = data.Select(e => e.Expected.Sentiment).Distinct().Order().ToList();
    }

    /// <summary>Creates the client for a dataset with at least <see cref="BestCorrect"/> rows.</summary>
    public static ScriptedChatClient Create(IReadOnlyList<LabeledExample<SentimentSig>> data)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfLessThan(data.Count, BestCorrect);
        return new ScriptedChatClient(data);
    }

    /// <summary>Every call made, in order.</summary>
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

    [GeneratedRegex(@"candidate (\d+) of")]
    private static partial Regex CandidateNumber();

    string ReplyFor(IReadOnlyList<ChatMessage> messages)
    {
        var system = messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Text ?? "";
        var user = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";

        if (user.Contains("Infer concise rules", StringComparison.Ordinal))
            return "[[ ## Rules ## ]]\n" + Rules;

        if (CandidateNumber().Match(user) is { Success: true } candidate)
            return candidate.Groups[1].Value == "1" ? WeakProposal : BestProposal;

        var row = RowOf(user);
        if (row < 0)
            return "unrecognised request";

        var correct = Math.Min(_data.Count, BaseCorrect(system) + CorrectDemos(messages));
        var right = _data[row].Expected.Sentiment;
        var label = row < correct ? right : _labels.First(l => l != right);
        return new ChatAdapter().FormatResponse(
            SignatureMetadataCache.For<SentimentSig>(),
            new Dictionary<string, object?> { [nameof(SentimentSig.Sentiment)] = label }).Text;
    }

    static int BaseCorrect(string system) =>
        system.Contains(BestProposal, StringComparison.Ordinal) ? BestCorrect
        : system.Contains(Rules, StringComparison.Ordinal) ? RulesCorrect
        : system.Contains(WeakProposal, StringComparison.Ordinal) ? WeakCorrect
        : BaselineCorrect;

    int RowOf(string text)
    {
        for (var i = 0; i < _data.Count; i++)
            if (text.Contains(_data[i].Inputs.Feedback, StringComparison.Ordinal))
                return i;
        return -1;
    }

    // A demo is a user message followed by an assistant message carrying its label.
    int CorrectDemos(IReadOnlyList<ChatMessage> messages)
    {
        var correct = 0;
        for (var i = 1; i < messages.Count - 1; i++)
        {
            if (messages[i].Role != ChatRole.Assistant || messages[i - 1].Role != ChatRole.User)
                continue;
            var row = RowOf(messages[i - 1].Text);
            if (row >= 0 && messages[i].Text.Contains(_data[row].Expected.Sentiment, StringComparison.Ordinal))
                correct++;
        }
        return correct;
    }
}

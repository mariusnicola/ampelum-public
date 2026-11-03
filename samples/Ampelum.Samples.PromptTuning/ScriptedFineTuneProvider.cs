using Microsoft.Extensions.AI;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Evaluation;
using SignalGrid.Ampelum.Optimizers;

namespace Ampelum.Samples.PromptTuning;

/// <summary>
/// A scripted <see cref="IFineTuneProvider"/>: it keeps every request and returns a classifier that answers the
/// first <see cref="TunedCorrect"/> rows right. No model is trained. The library ships no real provider.
/// </summary>
public sealed class ScriptedFineTuneProvider : IFineTuneProvider
{
    /// <summary>Rows the "tuned" classifier answers right without demonstrations.</summary>
    public const int TunedCorrect = 7;

    readonly IReadOnlyList<LabeledExample<SentimentSig>> _data;
    readonly List<FineTuneRequest> _requests = [];
    readonly List<ScriptedChatClient> _clients = [];

    /// <summary>Creates the provider.</summary>
    /// <param name="data">The labeled rows the tuned classifier answers.</param>
    public ScriptedFineTuneProvider(IReadOnlyList<LabeledExample<SentimentSig>> data)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
    }

    /// <summary>Every request received, in order.</summary>
    public IReadOnlyList<FineTuneRequest> Requests => _requests;

    /// <summary>The tuned clients returned, in order.</summary>
    public IReadOnlyList<ScriptedChatClient> Clients => _clients;

    /// <inheritdoc />
    public Task<IChatClient> FineTuneAsync(FineTuneRequest request, CancellationToken ct = default)
    {
        _requests.Add(request);
        var client = ScriptedChatClient.Create(_data, baselineCorrect: TunedCorrect);
        _clients.Add(client);
        return Task.FromResult<IChatClient>(client);
    }
}

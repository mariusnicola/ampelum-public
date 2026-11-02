using System.Text.Json;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Testing;

/// <summary>Records LLM responses on first call, replays on subsequent calls. Zero API calls on replay.</summary>
public class RecordingChatClient : IChatClient
{
    readonly IChatClient _inner;
    readonly string _recordingsPath;
    readonly List<string> _recordings = [];
    int _callIndex;
    bool _loaded;
    bool _recordedInSession;

    /// <inheritdoc/>
    public ChatClientMetadata Metadata { get; } = new(nameof(RecordingChatClient));

    /// <summary>Creates a new recording wrapper around the given chat client.</summary>
    /// <param name="inner">The real chat client to delegate to on first call.</param>
    /// <param name="recordingsPath">Path to the JSONL file for recorded responses.</param>
    public RecordingChatClient(IChatClient inner, string recordingsPath)
    {
        _inner = inner;
        _recordingsPath = recordingsPath;
    }

    /// <inheritdoc/>
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        await LoadAsync();
        if (!_recordedInSession && _callIndex < _recordings.Count)
            return JsonSerializer.Deserialize<ChatResponse>(_recordings[_callIndex++])!;

        var response = await _inner.GetResponseAsync(messages, options, cancellationToken);
        _recordings.Add(JsonSerializer.Serialize(response));
        _recordedInSession = true;
        await SaveAsync();
        return response;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await LoadAsync();
        if (!_recordedInSession && _callIndex < _recordings.Count)
        {
            var recorded = JsonSerializer.Deserialize<ChatResponse>(_recordings[_callIndex++])!;
            var recordedText = GetRecordedText(recorded);
            if (recordedText is null)
                yield break;

            foreach (var ch in recordedText)
                yield return new ChatResponseUpdate(ChatRole.Assistant, ch.ToString());
            yield break;
        }

        var responseText = "";
        await foreach (var update in _inner.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            responseText += update.Text;
            yield return update;
        }
        var finalResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText));
        _recordings.Add(JsonSerializer.Serialize(finalResponse));
        _recordedInSession = true;
        await SaveAsync();
    }

    async Task LoadAsync()
    {
        if (_loaded || !File.Exists(_recordingsPath)) { _loaded = true; return; }
        try
        {
            foreach (var line in await File.ReadAllLinesAsync(_recordingsPath))
                if (!string.IsNullOrWhiteSpace(line))
                    _recordings.Add(line);
            _recordedInSession = false; // loaded from disk — can replay
        }
        catch { /* corrupt file — start fresh */ }
        _loaded = true;
    }

    async Task SaveAsync() =>
        await File.WriteAllTextAsync(_recordingsPath, string.Join(Environment.NewLine, _recordings));

    /// <summary>
    /// Returns the recorded text used for replay. Overridable for branch tests.
    /// </summary>
    protected virtual string? GetRecordedText(ChatResponse recorded) => recorded.Text;

    /// <inheritdoc/>
    public void Dispose() { }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
}

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Agents.Memory;

/// <summary>
/// Conversation memory that LLM-summarises older turns when the buffer exceeds <c>threshold</c>.
/// Each summarisation collapses the oldest <c>windowSize</c> entries into one synthetic message
/// (sender <c>episodic-summary</c>, role <c>system-summary</c>).
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class EpisodicMemory : IConversationMemory
{
    readonly IChatClient _chat;
    readonly int _threshold;
    readonly int _windowSize;
    readonly List<AgentMessage> _buffer = [];
    readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Initialises a new episodic memory.</summary>
    public EpisodicMemory(IChatClient chat, int threshold = 10, int windowSize = 10)
    {
        ArgumentNullException.ThrowIfNull(chat);
        if (threshold <= 0)
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "Threshold must be positive.");
        if (windowSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(windowSize), windowSize, "Window size must be positive.");
        if (windowSize > threshold)
            throw new ArgumentOutOfRangeException(nameof(windowSize), windowSize, "Window size must be <= threshold.");
        _chat = chat;
        _threshold = threshold;
        _windowSize = windowSize;
    }

    /// <inheritdoc/>
    public async ValueTask AppendAsync(AgentMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _buffer.Add(message);
            if (_buffer.Count > _threshold)
            {
                var window = _buffer.GetRange(0, _windowSize);
                var sb = new StringBuilder();
                foreach (var m in window)
                    sb.Append(m.SenderName).Append(" (").Append(m.Role).Append("): ").AppendLine(m.Content);

                var predict = Predict.Create<SummarizationSig>(_chat);
                var result = await predict.ForwardAsync(
                    new SummarizationSig { Conversation = sb.ToString() },
                    demos: null,
                    ct: cancellationToken).ConfigureAwait(false);

                _buffer.RemoveRange(0, _windowSize);
                _buffer.Insert(0, new AgentMessage(
                    "episodic-summary",
                    "system-summary",
                    result.Value.Summary)
                {
                    Metadata = new Dictionary<string, string>
                    {
                        ["window-size"] = _windowSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    },
                });
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<AgentMessage>> GetRecentAsync(int n, CancellationToken cancellationToken)
    {
        if (n <= 0)
            return ValueTask.FromResult<IReadOnlyList<AgentMessage>>([]);
        AgentMessage[] snapshot;
        lock (_buffer) snapshot = [.. _buffer];
        var take = Math.Min(n, snapshot.Length);
        var result = new AgentMessage[take];
        Array.Copy(snapshot, snapshot.Length - take, result, 0, take);
        return ValueTask.FromResult<IReadOnlyList<AgentMessage>>(result);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<AgentMessage>> QueryAsync(string query, int topK, CancellationToken cancellationToken) =>
        GetRecentAsync(topK, cancellationToken);
}

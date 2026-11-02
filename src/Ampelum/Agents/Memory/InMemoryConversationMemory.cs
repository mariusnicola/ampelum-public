using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SignalGrid.Ampelum.Agents.Memory;

/// <summary>
/// Bounded ring-buffer conversation memory. Oldest entries are evicted FIFO once capacity is reached.
/// <see cref="QueryAsync"/> falls back to recency (no semantic search at this tier).
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class InMemoryConversationMemory : IConversationMemory
{
    readonly int _capacity;
    readonly Queue<AgentMessage> _queue;
    readonly object _lock = new();

    /// <summary>Initialises a new memory with the given strict capacity.</summary>
    public InMemoryConversationMemory(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        _capacity = capacity;
        _queue = new Queue<AgentMessage>(capacity);
    }

    /// <inheritdoc/>
    public ValueTask AppendAsync(AgentMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_lock)
        {
            _queue.Enqueue(message);
            while (_queue.Count > _capacity)
                _queue.Dequeue();
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<AgentMessage>> GetRecentAsync(int n, CancellationToken cancellationToken)
    {
        if (n <= 0)
            return ValueTask.FromResult<IReadOnlyList<AgentMessage>>([]);
        AgentMessage[] snapshot;
        lock (_lock) snapshot = [.. _queue];
        var take = Math.Min(n, snapshot.Length);
        var result = new AgentMessage[take];
        Array.Copy(snapshot, snapshot.Length - take, result, 0, take);
        return ValueTask.FromResult<IReadOnlyList<AgentMessage>>(result);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<AgentMessage>> QueryAsync(string query, int topK, CancellationToken cancellationToken) =>
        GetRecentAsync(topK, cancellationToken);
}

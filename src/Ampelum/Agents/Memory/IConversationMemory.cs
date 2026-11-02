using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace SignalGrid.Ampelum.Agents.Memory;

/// <summary>
/// Pluggable conversation memory backend â€” append-only with recent and semantic-query reads.
/// Implementations include: short-term ring buffer (<see cref="InMemoryConversationMemory"/>),
/// vector-store-backed (<c>VectorConversationMemory</c>), episodic LLM-summarised
/// (<c>EpisodicMemory</c>), and composite tiers (<c>CompositeConversationMemory</c>).
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IConversationMemory
{
    /// <summary>Appends one message to memory. Implementations may evict older entries to honour capacity.</summary>
    ValueTask AppendAsync(AgentMessage message, CancellationToken cancellationToken);

    /// <summary>Returns up to <paramref name="n"/> most-recent messages in chronological order.</summary>
    ValueTask<IReadOnlyList<AgentMessage>> GetRecentAsync(int n, CancellationToken cancellationToken);

    /// <summary>
    /// Returns up to <paramref name="topK"/> messages most relevant to <paramref name="query"/>.
    /// Tiers without semantic search may fall back to recency.
    /// </summary>
    ValueTask<IReadOnlyList<AgentMessage>> QueryAsync(string query, int topK, CancellationToken cancellationToken);
}

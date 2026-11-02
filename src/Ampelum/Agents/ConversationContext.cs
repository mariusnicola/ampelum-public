using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.Agents.Memory;

namespace SignalGrid.Ampelum.Agents;

/// <summary>
/// Read-only view of the conversation seen by a speaker selector or agent.
/// Mutating the source collection passed at construction does not affect the snapshot.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record ConversationContext
{
    /// <summary>Initialises a new context, defensively copying <paramref name="history"/>.</summary>
    public ConversationContext(IEnumerable<AgentMessage> history, IConversationMemory? memory, int turnIndex)
    {
        History = history is null ? [] : [.. history];
        Memory = memory;
        TurnIndex = turnIndex;
    }

    /// <summary>Ordered conversation history â€” index 0 is the earliest message.</summary>
    public IReadOnlyList<AgentMessage> History { get; }

    /// <summary>Optional pluggable memory backend for cross-turn recall. May be <see langword="null"/>.</summary>
    public IConversationMemory? Memory { get; }

    /// <summary>Zero-based turn counter â€” the orchestrator increments it once per agent turn.</summary>
    public int TurnIndex { get; }
}

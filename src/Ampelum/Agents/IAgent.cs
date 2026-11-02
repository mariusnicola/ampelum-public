using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace SignalGrid.Ampelum.Agents;

/// <summary>
/// One participant in a multi-agent conversation. Implementations encapsulate prompt logic,
/// tool wiring, and any internal state. The orchestrator drives them via <see cref="ReplyAsync"/>.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IAgent
{
    /// <summary>Stable, machine-readable identifier shown to selectors and used for handoff routing.</summary>
    string Name { get; }

    /// <summary>Human-readable role label (e.g. <c>planner</c>, <c>coder</c>, <c>critic</c>).</summary>
    string Role { get; }

    /// <summary>Produces the agent's next message given the current conversation context.</summary>
    ValueTask<AgentMessage> ReplyAsync(ConversationContext ctx, CancellationToken cancellationToken);
}

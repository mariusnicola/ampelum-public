using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace SignalGrid.Ampelum.Agents.SpeakerSelectors;

/// <summary>
/// Picks the next agent to speak in a multi-agent conversation. Implementations may use
/// round-robin, role rotation, an LLM coordinator, or any custom policy.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface ISpeakerSelector
{
    /// <summary>Selects one agent from <paramref name="agents"/> to take the next turn.</summary>
    ValueTask<IAgent> SelectAsync(ConversationContext ctx, IReadOnlyList<IAgent> agents, CancellationToken cancellationToken);
}

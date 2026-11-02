using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace SignalGrid.Ampelum.Agents.SpeakerSelectors;

/// <summary>
/// Picks agents in strict round-robin order indexed by <see cref="ConversationContext.TurnIndex"/>.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class RoundRobinSelector : ISpeakerSelector
{
    /// <inheritdoc/>
    public ValueTask<IAgent> SelectAsync(ConversationContext ctx, IReadOnlyList<IAgent> agents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agents);
        if (agents.Count == 0)
            throw new ArgumentException("At least one agent required.", nameof(agents));
        cancellationToken.ThrowIfCancellationRequested();
        var idx = ((ctx?.TurnIndex ?? 0) % agents.Count + agents.Count) % agents.Count;
        return ValueTask.FromResult(agents[idx]);
    }
}

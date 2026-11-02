using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SignalGrid.Ampelum.Agents.SpeakerSelectors;

/// <summary>
/// Picks agents by rotating through a fixed role sequence. The Nth turn selects the first agent
/// whose <see cref="IAgent.Role"/> matches <c>roles[N % roles.Count]</c>.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class RoleBasedSelector : ISpeakerSelector
{
    readonly IReadOnlyList<string> _roles;

    /// <summary>Initialises the selector with a non-empty rotation of role names.</summary>
    public RoleBasedSelector(IReadOnlyList<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        if (roles.Count == 0)
            throw new ArgumentException("Role sequence must contain at least one role.", nameof(roles));
        _roles = [.. roles];
    }

    /// <inheritdoc/>
    public ValueTask<IAgent> SelectAsync(ConversationContext ctx, IReadOnlyList<IAgent> agents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agents);
        if (agents.Count == 0)
            throw new ArgumentException("At least one agent required.", nameof(agents));
        cancellationToken.ThrowIfCancellationRequested();
        var idx = ((ctx?.TurnIndex ?? 0) % _roles.Count + _roles.Count) % _roles.Count;
        var role = _roles[idx];
        var pick = agents.FirstOrDefault(a => string.Equals(a.Role, role, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"No agent with role '{role}' available.");
        return ValueTask.FromResult(pick);
    }
}

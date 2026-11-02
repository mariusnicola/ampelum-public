using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace SignalGrid.Ampelum.Agents.Memory;

/// <summary>
/// Composes multiple <see cref="IConversationMemory"/> tiers. Append fans out to every tier;
/// <see cref="GetRecentAsync"/> defers to the first tier; <see cref="QueryAsync"/> merges results
/// from every tier, deduplicates by <c>(SenderName, Timestamp)</c>, and trims to <c>topK</c>.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class CompositeConversationMemory : IConversationMemory
{
    readonly IReadOnlyList<IConversationMemory> _tiers;

    /// <summary>Initialises the composite over the given non-empty tier list.</summary>
    public CompositeConversationMemory(params IConversationMemory[] tiers)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        if (tiers.Length == 0)
            throw new ArgumentException("At least one tier required.", nameof(tiers));
        _tiers = [.. tiers];
    }

    /// <inheritdoc/>
    public async ValueTask AppendAsync(AgentMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        foreach (var t in _tiers)
            await t.AppendAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<AgentMessage>> GetRecentAsync(int n, CancellationToken cancellationToken) =>
        _tiers[0].GetRecentAsync(n, cancellationToken);

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<AgentMessage>> QueryAsync(string query, int topK, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (topK <= 0) return [];
        var seen = new HashSet<(string, DateTimeOffset)>();
        var merged = new List<AgentMessage>();
        foreach (var t in _tiers)
        {
            var hits = await t.QueryAsync(query, topK, cancellationToken).ConfigureAwait(false);
            foreach (var h in hits)
            {
                if (seen.Add((h.SenderName, h.Timestamp)))
                    merged.Add(h);
                if (merged.Count >= topK) return merged;
            }
        }
        return merged;
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SignalGrid.Ampelum.Agents.SpeakerSelectors;

/// <summary>
/// Picks the next speaker by asking an LLM coordinator. Falls back to round-robin if the
/// returned name does not match any agent.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class LlmDrivenSelector : ISpeakerSelector
{
    readonly IChatClient _chat;
    readonly ILogger<LlmDrivenSelector> _logger;
    readonly RoundRobinSelector _fallback = new();

    /// <summary>Initialises the selector with an LLM coordinator and optional logger for fallback warnings.</summary>
    public LlmDrivenSelector(IChatClient chatClient, ILogger<LlmDrivenSelector>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        _chat = chatClient;
        _logger = logger ?? NullLogger<LlmDrivenSelector>.Instance;
    }

    /// <inheritdoc/>
    public async ValueTask<IAgent> SelectAsync(ConversationContext ctx, IReadOnlyList<IAgent> agents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agents);
        if (agents.Count == 0)
            throw new ArgumentException("At least one agent required.", nameof(agents));
        cancellationToken.ThrowIfCancellationRequested();

        var historyText = SerializeHistory(ctx);
        var agentsText = SerializeAgents(agents);

        var predict = Predict.Create<SelectSpeakerSig>(_chat);
        var result = await predict.ForwardAsync(
            new SelectSpeakerSig { History = historyText, AvailableAgents = agentsText },
            demos: null,
            ct: cancellationToken).ConfigureAwait(false);

        var pickName = (result.Value.NextSpeaker ?? "").Trim();
        var match = agents.FirstOrDefault(a => string.Equals(a.Name, pickName, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            return match;

        _logger.LogWarning(
            "LlmDrivenSelector: LLM returned unknown speaker '{PickName}'; falling back to round-robin.",
            pickName);
        return await _fallback.SelectAsync(ctx, agents, cancellationToken).ConfigureAwait(false);
    }

    static string SerializeHistory(ConversationContext? ctx)
    {
        if (ctx is null || ctx.History.Count == 0) return "(empty)";
        var sb = new StringBuilder();
        var start = Math.Max(0, ctx.History.Count - 6);
        for (var i = start; i < ctx.History.Count; i++)
        {
            var m = ctx.History[i];
            sb.Append(m.SenderName).Append(" (").Append(m.Role).Append("): ").AppendLine(m.Content);
        }
        return sb.ToString();
    }

    static string SerializeAgents(IReadOnlyList<IAgent> agents)
    {
        var sb = new StringBuilder();
        foreach (var a in agents)
            sb.Append(a.Name).Append(" (").Append(a.Role).AppendLine(")");
        return sb.ToString();
    }
}

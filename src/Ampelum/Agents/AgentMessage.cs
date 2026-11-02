using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Agents;

/// <summary>
/// One message in a multi-agent conversation. Immutable record.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record AgentMessage(string SenderName, string Role, string Content)
{
    /// <summary>Optional handoff target â€” the orchestrator routes the next turn to this agent name when set.</summary>
    public string? Handoff { get; init; }

    /// <summary>True when this message ends the conversation. The orchestrator stops after appending it.</summary>
    public bool IsTerminal { get; init; }

    /// <summary>UTC timestamp at which the message was produced. Defaults to <see cref="DateTimeOffset.UtcNow"/>.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Free-form metadata bag â€” opaque to the orchestrator, useful for tracing/debug.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>();
}

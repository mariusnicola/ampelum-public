using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Batch;

/// <summary>
/// A single item in a batch submission (PSQ-4, v0.9g).
/// </summary>
/// <param name="CustomId">Caller-supplied identifier echoed back on the matching <see cref="BatchResultItem"/>.</param>
/// <param name="Messages">The chat messages for this item.</param>
/// <param name="Options">Optional per-item chat options; falls back to the batch default.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record BatchRequest(string CustomId, IReadOnlyList<ChatMessage> Messages, ChatOptions? Options = null);

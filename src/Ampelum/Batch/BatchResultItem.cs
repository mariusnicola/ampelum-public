using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Batch;

/// <summary>
/// One result emitted by <see cref="IBatchChatClient.StreamResultsAsync"/>. Either
/// <see cref="Response"/> is non-null (success) or <see cref="ErrorMessage"/> is non-null (per-item failure).
/// </summary>
/// <param name="CustomId">Caller-supplied id from the matching <see cref="BatchRequest"/>.</param>
/// <param name="Response">Chat response if the item succeeded.</param>
/// <param name="ErrorMessage">Provider-reported error message if the item failed.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record BatchResultItem(string CustomId, ChatResponse? Response, string? ErrorMessage);

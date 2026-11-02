using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Batch;

/// <summary>
/// Opaque handle returned by <see cref="IBatchChatClient.SubmitAsync"/>. The handle
/// identifies the batch on the provider side and carries metadata for audit (PSQ-4, v0.9g).
/// </summary>
/// <param name="Id">Provider-assigned batch identifier.</param>
/// <param name="Provider">Symbolic provider name (e.g. <c>"openai"</c>, <c>"anthropic"</c>).</param>
/// <param name="SubmittedAt">UTC submission timestamp.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record BatchHandle(string Id, string Provider, DateTimeOffset SubmittedAt);

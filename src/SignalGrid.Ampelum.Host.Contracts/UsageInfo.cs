using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>Token and cost metrics for a completed forward call. Spec §6.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record UsageInfo(
    long PromptTokens,
    long CompletionTokens,
    decimal CostCents,
    double DurationMs);

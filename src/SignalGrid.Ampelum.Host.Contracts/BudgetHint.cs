using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>Optional per-call budget cap applied via the host's <c>BudgetHook</c>. Spec §6.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record BudgetHint(
    long? TokenLimit = null,
    decimal? CostCentsLimit = null,
    int? CallLimit = null,
    int? DeadlineSeconds = null);

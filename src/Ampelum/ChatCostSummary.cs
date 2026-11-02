using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum;

/// <summary>
/// Aggregated token usage and optional estimated cost for one or more LM chat calls.
/// Used by <c>StreamingChatSession</c> to report per-turn and session-total costs.
/// </summary>
/// <param name="InputTokens">Total prompt (input) tokens consumed.</param>
/// <param name="OutputTokens">Total completion (output) tokens produced.</param>
/// <param name="EstimatedCostUsd">Estimated cost in USD, or <see langword="null"/> when rates are unavailable.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public record ChatCostSummary(long InputTokens, long OutputTokens, decimal? EstimatedCostUsd)
{
    /// <summary>A zero-valued <see cref="ChatCostSummary"/> (identity element for <c>+</c>).</summary>
    public static readonly ChatCostSummary Zero = new(0, 0, 0m);

    /// <summary>Adds two summaries, propagating cost only when at least one operand carries a value.</summary>
    public static ChatCostSummary operator +(ChatCostSummary a, ChatCostSummary b) =>
        new(a.InputTokens + b.InputTokens,
            a.OutputTokens + b.OutputTokens,
            a.EstimatedCostUsd.HasValue || b.EstimatedCostUsd.HasValue
                ? (a.EstimatedCostUsd ?? 0m) + (b.EstimatedCostUsd ?? 0m)
                : null);
}

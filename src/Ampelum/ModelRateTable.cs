using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum;

/// <summary>
/// Per-token pricing for a specific model, used to estimate USD cost from token counts.
/// Fetch rates from the provider API and cache externally; pass an instance to
/// <c>StreamingChatSession</c> to enable cost tracking.
/// </summary>
/// <param name="InputPerToken">USD cost per input (prompt) token.</param>
/// <param name="OutputPerToken">USD cost per output (completion) token.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public record ModelRateTable(decimal InputPerToken, decimal OutputPerToken)
{
    /// <summary>Estimates the total cost in USD for the given token counts.</summary>
    public decimal Estimate(long inputTokens, long outputTokens) =>
        inputTokens * InputPerToken + outputTokens * OutputPerToken;

    /// <summary>Estimates the total cost in USD for the given model and token counts.
    /// The modelId parameter is accepted for API compatibility but unused in single-model instances.</summary>
    public decimal EstimateCost(string modelId, long inputTokens, long outputTokens) =>
        Estimate(inputTokens, outputTokens);
}

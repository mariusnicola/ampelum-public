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

    /// <summary>Rates for named models, matched to the model id exactly (ordinal). A model not listed here is priced
    /// with this table's own <see cref="InputPerToken"/> and <see cref="OutputPerToken"/>.</summary>
    public IReadOnlyDictionary<string, ModelRateTable>? PerModel { get; init; }

    /// <summary>Estimates the total cost in USD for the given model and token counts, using the rates listed for
    /// <paramref name="modelId"/> in <see cref="PerModel"/>, or this table's own pair when the model is not listed.</summary>
    public decimal EstimateCost(string modelId, long inputTokens, long outputTokens) =>
        PerModel is not null && PerModel.TryGetValue(modelId, out var rates)
            ? rates.Estimate(inputTokens, outputTokens)
            : Estimate(inputTokens, outputTokens);
}

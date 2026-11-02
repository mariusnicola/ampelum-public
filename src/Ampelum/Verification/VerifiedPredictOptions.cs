using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum;

/// <summary>
/// Configuration for <see cref="VerifiedPredict{TSig}"/> retry and failure behavior.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class VerifiedPredictOptions
{
    /// <summary>
    /// Maximum number of retries after a critic rejection. Default is 2.
    /// Total attempts = 1 initial + MaxRetries = 3 by default.
    /// </summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>
    /// When true, throws <see cref="VerificationFailedException"/> after exhausting retries.
    /// When false, returns the last output regardless of approval status. Default is true.
    /// </summary>
    public bool ThrowOnRejection { get; init; } = true;

    /// <summary>
    /// When true, returns the last generated output even if all attempts were rejected.
    /// Requires ThrowOnRejection to be false. Default is false.
    /// </summary>
    public bool ReturnLastOnExhaustion { get; init; } = false;
}

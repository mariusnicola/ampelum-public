using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum;

/// <summary>
/// Thrown when <see cref="VerifiedPredict{TSig}"/> exhausts all retry attempts and a critic continues
/// to reject the output, and <see cref="VerifiedPredictOptions.ThrowOnRejection"/> is true.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class VerificationFailedException : Exception
{
    /// <summary>
    /// The number of prediction attempts made before giving up (includes the initial attempt).
    /// </summary>
    public int AttemptCount { get; }

    /// <summary>
    /// The final <see cref="CriticVerdict"/> from the last rejected attempt.
    /// </summary>
    public CriticVerdict LastVerdict { get; }

    /// <summary>
    /// Initializes a new instance with the attempt count and final verdict.
    /// </summary>
    /// <param name="attemptCount">Number of attempts made.</param>
    /// <param name="lastVerdict">The final verdict that caused the exception.</param>
    public VerificationFailedException(int attemptCount, CriticVerdict lastVerdict)
        : base($"Verification failed after {attemptCount} attempt(s). Last rejection: {lastVerdict.Reasoning}")
    {
        AttemptCount = attemptCount;
        LastVerdict = lastVerdict;
    }
}

using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum;

/// <summary>
/// Evaluates whether a generated output meets quality criteria. Used by <see cref="VerifiedPredict{TSig}"/>
/// to accept or reject outputs before returning them to the caller.
/// </summary>
/// <typeparam name="TSig">The signature type being predicted.</typeparam>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface ICriticPolicy<TSig> where TSig : class
{
    /// <summary>
    /// Asynchronously evaluates the quality of a generator output.
    /// </summary>
    /// <param name="input">The input example (input field values).</param>
    /// <param name="generatorOutput">The output produced by the generator.</param>
    /// <param name="context">The ambient Ampelum context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A verdict indicating approval, rejection, and optional feedback.</returns>
    Task<CriticVerdict> EvaluateAsync(
        Example input,
        Example generatorOutput,
        AmpelumContext context,
        CancellationToken ct = default);
}

/// <summary>
/// The result of a critic's evaluation. Indicates whether an output was approved,
/// and if rejected, provides a reason and optional suggestions for improvement.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record CriticVerdict(
    bool Approved,
    string Reasoning,
    CriticRejectionReason? RejectionReason = null,
    IReadOnlyList<string>? Suggestions = null
);

/// <summary>
/// Classifies the reason for an output rejection.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public enum CriticRejectionReason
{
    /// <summary>Output violates the expected schema or structure.</summary>
    SchemaViolation,
    /// <summary>Output contains factual errors or incorrect information.</summary>
    FactualError,
    /// <summary>Output violates safety or policy constraints.</summary>
    SafetyViolation,
    /// <summary>Output does not meet quality threshold.</summary>
    QualityBelowThreshold,
    /// <summary>Custom or application-specific rejection reason.</summary>
    Custom
}

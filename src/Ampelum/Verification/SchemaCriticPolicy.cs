using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum;

/// <summary>
/// A deterministic critic that validates the structure of generator outputs. Since <see cref="Predict{TSig}"/>
/// already validates schema via the adapter (throwing if the LM output doesn't parse), a received
/// <see cref="Prediction{TSig}"/> already passed structural validation. This critic approves outputs
/// that have all required output fields present and non-null.
/// </summary>
/// <typeparam name="TSig">The signature type being predicted.</typeparam>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class SchemaCriticPolicy<TSig> : ICriticPolicy<TSig> where TSig : class
{
    /// <summary>
    /// Evaluates whether the output has all required fields present and non-null.
    /// </summary>
    public Task<CriticVerdict> EvaluateAsync(
        Example input,
        Example generatorOutput,
        AmpelumContext context,
        CancellationToken ct = default)
    {
        // Output fields must be present and non-null
        if (generatorOutput.Outputs.Count == 0)
        {
            return Task.FromResult(new CriticVerdict(
                Approved: false,
                Reasoning: "Generator output has no output fields.",
                RejectionReason: CriticRejectionReason.SchemaViolation,
                Suggestions: new[] { "Ensure the generator produces all required output fields." }
            ));
        }

        bool hasNullOutput = generatorOutput.Outputs.Values.Any(v => v is null);
        if (hasNullOutput)
        {
            return Task.FromResult(new CriticVerdict(
                Approved: false,
                Reasoning: "Generator output contains null output fields.",
                RejectionReason: CriticRejectionReason.SchemaViolation,
                Suggestions: new[] { "Ensure all output fields have non-null values." }
            ));
        }

        return Task.FromResult(new CriticVerdict(
            Approved: true,
            Reasoning: "Schema validation passed — all output fields present and non-null."
        ));
    }
}

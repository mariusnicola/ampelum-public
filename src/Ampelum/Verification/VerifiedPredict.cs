using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace SignalGrid.Ampelum;

/// <summary>
/// A wrapper around <see cref="Predict{TSig}"/> that adds critic-based verification. Calls the
/// generator, passes the output to a critic policy, and retries if rejected, up to a configurable
/// limit. Optionally throws or returns the final output on exhaustion.
/// </summary>
/// <typeparam name="TSig">The signature type being predicted. Must be a class type.</typeparam>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class VerifiedPredict<TSig> where TSig : class, ISignature
{
    private readonly Predict<TSig> _generator;
    private readonly ICriticPolicy<TSig> _criticPolicy;
    private readonly VerifiedPredictOptions _options;

    /// <summary>
    /// Initializes a verified predictor with a generator, critic, and optional configuration.
    /// </summary>
    /// <param name="generator">The underlying predictor to call. Must not be null.</param>
    /// <param name="criticPolicy">The policy to evaluate outputs. Must not be null.</param>
    /// <param name="options">Configuration for retries and failure modes. If null, defaults are used.</param>
    public VerifiedPredict(
        Predict<TSig> generator,
        ICriticPolicy<TSig> criticPolicy,
        VerifiedPredictOptions? options = null)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _criticPolicy = criticPolicy ?? throw new ArgumentNullException(nameof(criticPolicy));
        _options = options ?? new VerifiedPredictOptions();
    }

    /// <summary>
    /// Runs the generator and critic in a retry loop. Returns an Example containing the
    /// final (or approved) output.
    /// </summary>
    /// <param name="input">The input example to pass to the generator.</param>
    /// <param name="context">The ambient Ampelum context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An Example containing the approved or final output.</returns>
    /// <exception cref="VerificationFailedException">
    /// Thrown when all attempts are exhausted, the last attempt is rejected, and
    /// <see cref="VerifiedPredictOptions.ThrowOnRejection"/> is true.
    /// </exception>
    public async Task<Example> PredictAsync(
        Example input,
        AmpelumContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        CriticVerdict? lastVerdict = null;
        Example? lastOutput = null;

        for (int attempt = 0; attempt <= _options.MaxRetries; attempt++)
        {
            // Reconstruct TSig from input Example using reflection
            var sig = ReconstructSignature(input, lastVerdict?.Suggestions);

            // Run generator
            var prediction = await _generator.ForwardAsync(sig, ct: ct).ConfigureAwait(false);

            // Extract outputs as Example
            lastOutput = Example.From(prediction.Value);

            // Run critic
            lastVerdict = await _criticPolicy.EvaluateAsync(input, lastOutput, context, ct).ConfigureAwait(false);

            if (lastVerdict.Approved)
                return lastOutput;
        }

        // Exhausted retries
        if (_options.ReturnLastOnExhaustion && lastOutput is not null)
            return lastOutput;

        if (_options.ThrowOnRejection)
            throw new VerificationFailedException(_options.MaxRetries + 1, lastVerdict!);

        return lastOutput!;
    }

    private static TSig ReconstructSignature(Example input, IReadOnlyList<string>? suggestions)
    {
        // Create instance via parameterless constructor or Activator
        var instance = (TSig)Activator.CreateInstance(typeof(TSig))!;

        // Set input properties via reflection
        var props = typeof(TSig).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var (key, value) in input.Inputs)
        {
            var prop = props.FirstOrDefault(p =>
                string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
            if (prop?.CanWrite == true)
                prop.SetValue(instance, value);
        }

        // If there are suggestions from a previous rejection, inject them as a hint
        // by looking for a property named "Hint" or "Context" or similar
        if (suggestions?.Count > 0)
        {
            var hintProp = props.FirstOrDefault(p =>
                string.Equals(p.Name, "Hint", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.Name, "Context", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.Name, "PreviousFeedback", StringComparison.OrdinalIgnoreCase));
            if (hintProp?.CanWrite == true)
                hintProp.SetValue(instance, string.Join("\n", suggestions));
        }

        return instance;
    }
}

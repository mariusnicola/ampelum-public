using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum;

/// <summary>
/// Composes multiple critic policies, evaluating each in sequence. The first policy to reject
/// the output short-circuits the chain and returns that rejection. Only if all policies
/// approve does the composite return approval.
/// </summary>
/// <typeparam name="TSig">The signature type being predicted.</typeparam>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class CompositeCriticPolicy<TSig> : ICriticPolicy<TSig> where TSig : class
{
    private readonly IReadOnlyList<ICriticPolicy<TSig>> _policies;

    /// <summary>
    /// Initializes a composite policy with the given list of sub-policies.
    /// </summary>
    /// <param name="policies">The ordered list of policies to evaluate. Must not be null.</param>
    public CompositeCriticPolicy(IReadOnlyList<ICriticPolicy<TSig>> policies)
    {
        _policies = policies ?? throw new ArgumentNullException(nameof(policies));
    }

    /// <summary>
    /// Evaluates each policy in sequence, returning the first rejection or approval if all pass.
    /// </summary>
    public async Task<CriticVerdict> EvaluateAsync(
        Example input,
        Example generatorOutput,
        AmpelumContext context,
        CancellationToken ct = default)
    {
        foreach (var policy in _policies)
        {
            var verdict = await policy.EvaluateAsync(input, generatorOutput, context, ct).ConfigureAwait(false);
            if (!verdict.Approved)
                return verdict;
        }
        return new CriticVerdict(Approved: true, Reasoning: "All policies approved.");
    }
}

using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Composition;

/// <summary>Stops after at least <c>max</c> iterations have run.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class MaxIterations<T> : LoopPredicate<T> where T : ISignature
{
    readonly int _max;

    /// <summary>Initialises a <see cref="MaxIterations{T}"/> predicate that stops after <paramref name="max"/> iterations.</summary>
    public MaxIterations(int max)
    {
        if (max <= 0) throw new ArgumentOutOfRangeException(nameof(max), max, "must be positive");
        _max = max;
    }

    /// <inheritdoc/>
    public override bool ShouldStop(LoopState<T> state) => state.Iteration >= _max;

    /// <inheritdoc/>
    public override IReadOnlyDictionary<string, object?> Parameters => new Dictionary<string, object?> { ["max"] = _max };

    /// <inheritdoc/>
    public override LoopPredicate<T> WithParameters(IReadOnlyDictionary<string, object?> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var max = parameters.TryGetValue("max", out var v) && v is int i ? i : _max;
        return new MaxIterations<T>(max);
    }
}

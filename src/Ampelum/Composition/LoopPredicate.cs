using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Composition;

/// <summary>Direction operator for <see cref="FieldThreshold{T}"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public enum ThresholdDirection
{
    /// <summary>Stop when value &lt; threshold.</summary>
    LessThan = 0,
    /// <summary>Stop when value &lt;= threshold.</summary>
    LessThanOrEqual = 1,
    /// <summary>Stop when value &gt; threshold.</summary>
    GreaterThan = 2,
    /// <summary>Stop when value &gt;= threshold.</summary>
    GreaterThanOrEqual = 3,
}

/// <summary>Per-iteration loop state passed to <see cref="LoopPredicate{T}.ShouldStop"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record LoopState<T> where T : ISignature
{
    /// <summary>1-based iteration index (= LM calls actually made so far).</summary>
    public required int Iteration { get; init; }
    /// <summary>The current iteration's prediction value.</summary>
    public required T CurrentValue { get; init; }
    /// <summary>Whether this iteration tripped convergence (set by <see cref="LoopPredicate{T}.ShouldStop"/>'s caller).</summary>
    public bool IsConverged { get; init; }
    /// <summary>All predictions emitted so far in the loop, in order. Default empty.</summary>
    public IReadOnlyList<Prediction<T>> History { get; init; } = Array.Empty<Prediction<T>>();
}

/// <summary>
/// Optimizable convergence rule for <c>Loop&lt;TSig&gt;</c>. Implementors expose their tunable
/// parameters via <see cref="Parameters"/> (uniform <c>IReadOnlyDictionary&lt;string, object?&gt;</c>);
/// optimizers like MIPROv2/COPRO read those keys and rebind via <see cref="WithParameters"/>.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public abstract class LoopPredicate<T> where T : ISignature
{
    /// <summary>Decide whether the loop should stop after <paramref name="state"/>'s iteration.</summary>
    public abstract bool ShouldStop(LoopState<T> state);

    /// <summary>Optimizer-readable parameter map. Keys are stable across optimizer iterations
    /// for a given concrete subclass.</summary>
    public abstract IReadOnlyDictionary<string, object?> Parameters { get; }

    /// <summary>Returns a new instance of the same concrete subclass with parameters applied.
    /// MUST NOT mutate <c>this</c>.</summary>
    public abstract LoopPredicate<T> WithParameters(IReadOnlyDictionary<string, object?> parameters);
}

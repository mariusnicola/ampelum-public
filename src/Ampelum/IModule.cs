using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum;

/// <summary>
/// Canonical composition contract for v0.7+. Every v0.7 composition primitive
/// (Loop, Sequence, ConditionalBranch, WithConfig) and every retrofitted v0.6
/// module (Predict, BestOfN, Refine, Parallel, MultiChainComparison) implements
/// this interface. Sequence's hop slots, Loop's <c>inner</c>, and ConditionalBranch's
/// branches are typed against <see cref="IModule{TIn, TOut}"/> so any conforming
/// primitive can nest inside any other where the type parameters line up.
/// </summary>
/// <typeparam name="TIn">Input type. Contravariant â€” a module accepting a base input
/// type substitutes for a slot expecting a derived input type.</typeparam>
/// <typeparam name="TOut">Output type. Invariant â€” <see cref="Prediction{TOut}"/> is
/// invariant; widening <typeparamref name="TOut"/> would silently widen <c>Trace</c>
/// typing for downstream consumers.</typeparam>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IModule<in TIn, TOut>
{
    /// <summary>Run a single forward pass: feed <paramref name="inputs"/> through the
    /// module and return a <see cref="Prediction{TOut}"/>. Implementors must honour
    /// <paramref name="ct"/> at logical phase boundaries; an <see cref="OperationCanceledException"/>
    /// must propagate.</summary>
    Task<Prediction<TOut>> ForwardAsync(TIn inputs, CancellationToken ct = default);
}

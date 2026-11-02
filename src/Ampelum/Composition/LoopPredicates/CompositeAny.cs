using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Composition;

/// <summary>Short-circuiting OR over inner predicates. Empty array returns <see langword="false"/> (vacuous OR).</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class CompositeAny<T> : LoopPredicate<T> where T : ISignature
{
    readonly LoopPredicate<T>[] _inner;

    /// <summary>Initialises a <see cref="CompositeAny{T}"/> predicate over the given inner predicates.</summary>
    public CompositeAny(params LoopPredicate<T>[] inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <inheritdoc/>
    public override bool ShouldStop(LoopState<T> state)
    {
        for (var i = 0; i < _inner.Length; i++)
        {
            if (_inner[i].ShouldStop(state)) return true;
        }
        return false;
    }

    /// <inheritdoc/>
    public override IReadOnlyDictionary<string, object?> Parameters => new Dictionary<string, object?>
    {
        ["count"] = _inner.Length,
    };

    /// <inheritdoc/>
    public override LoopPredicate<T> WithParameters(IReadOnlyDictionary<string, object?> parameters)
        => new CompositeAny<T>(_inner);
}

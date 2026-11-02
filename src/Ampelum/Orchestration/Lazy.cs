using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Orchestration;

/// <summary>Deferred evaluation wrapper. Wraps a <see cref="Predict{TSig}"/> so the
/// LM call only fires once, on the first call to <see cref="ForwardAsync"/>.
/// Subsequent calls return the cached result.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class Lazy<TSig> : IModule<TSig, TSig> where TSig : ISignature
{
    readonly IModule<TSig, TSig> _inner;
    Prediction<TSig>? _cached;

    /// <summary>Creates a Lazy wrapper over <paramref name="inner"/>.</summary>
    public Lazy(IModule<TSig, TSig> inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <inheritdoc/>
    public async Task<Prediction<TSig>> ForwardAsync(TSig inputs, CancellationToken ct = default)
    {
        if (_cached is not null) return _cached;
        _cached = await _inner.ForwardAsync(inputs, ct);
        return _cached;
    }

    /// <summary>True after ForwardAsync has been called at least once.</summary>
    public bool IsEvaluated => _cached is not null;

    /// <summary>The cached prediction; null before first call.</summary>
    public Prediction<TSig>? Cached => _cached;
}
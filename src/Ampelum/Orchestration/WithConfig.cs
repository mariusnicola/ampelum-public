using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Orchestration;

/// <summary>
/// Configuration injection primitive. Applies a configuration delegate to the input state
/// before forwarding it to the inner module. Useful for setting budget, model selection,
/// or environment overrides at composition time.
/// </summary>
/// <typeparam name="TSig">Signature type whose input fields the config delegate may mutate.</typeparam>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class WithConfig<TSig> : IModule<TSig, TSig>
    where TSig : ISignature
{
    readonly IModule<TSig, TSig> _inner;
    readonly Action<TSig> _configApplier;

    /// <summary>
    /// Creates a configuration wrapper. The <paramref name="configApplier"/> is invoked on
    /// the input before forwarding to <paramref name="inner"/>.
    /// </summary>
    public WithConfig(IModule<TSig, TSig> inner, Action<TSig> configApplier)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(configApplier);
        _inner = inner;
        _configApplier = configApplier;
    }

    /// <inheritdoc/>
    public async Task<Prediction<TSig>> ForwardAsync(TSig inputs, CancellationToken ct = default)
    {
        // Apply configuration to the input state (clone if needed)
        _configApplier(inputs);

        // Forward to inner
        var innerResult = await _inner.ForwardAsync(inputs, ct);

        var extraMeta = new Dictionary<string, object?>
        {
            [Composition.TraceKeys.CompositionPrimitive] = "WithConfig",
        };
        var trace = Composition.CompositionTrace.Aggregate(
            id: Guid.NewGuid(),
            moduleName: $"WithConfig<{typeof(TSig).Name}>",
            signatureType: typeof(TSig),
            inputs: new Dictionary<string, object?> { ["configured"] = true },
            outputs: new Dictionary<string, object?> { ["result"] = innerResult.Value! },
            childTraces: new[] { innerResult.Trace },
            extraMetadata: extraMeta);

        return new Prediction<TSig>(innerResult.Value, trace);
    }
}
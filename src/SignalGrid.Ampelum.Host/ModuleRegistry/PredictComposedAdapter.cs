using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Optimizers;
using SignalGrid.Ampelum.Host.Internal;

namespace SignalGrid.Ampelum.Host.ModuleRegistry;

/// <summary>Wraps a <see cref="Predict{TSig}"/> as a single-submodule <see cref="IComposedModule"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class PredictComposedAdapter<TSig> : IComposedModule, IReplayableModule, IOptimizableModule where TSig : ISignature
{
    /// <summary>The wrapped Predict module.</summary>
    public Predict<TSig> Inner { get; }

    /// <summary>Initializes the adapter wrapping the given Predict module.</summary>
    public PredictComposedAdapter(Predict<TSig> inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        Inner = inner;
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, object> Submodules =>
        new Dictionary<string, object> { ["default"] = Inner };

    /// <inheritdoc/>
    public object Rebind(IReadOnlyDictionary<string, object> newSubmodules)
    {
        ArgumentNullException.ThrowIfNull(newSubmodules);
        if (!newSubmodules.TryGetValue("default", out var inner) || inner is not Predict<TSig> p)
            throw new InvalidOperationException(
                "PredictComposedAdapter requires submodule 'default' of type Predict<TSig>.");
        return new PredictComposedAdapter<TSig>(p);
    }

    bool IReplayableModule.AcceptsInputs(IReadOnlyDictionary<string, object?> inputs)
    {
        try { SignaturePropertyProjector.ProjectTo<TSig>(inputs); return true; }
        catch (ArgumentException) { return false; }
    }

    /// <inheritdoc/>
    async ValueTask<IReadOnlyDictionary<string, object?>> IReplayableModule.ReplayAsync(
        IReadOnlyDictionary<string, object?> inputs, AmpelumContext ctx, CancellationToken ct)
    {
        var sig = SignaturePropertyProjector.ProjectTo<TSig>(inputs);
        var prediction = await new Predict<TSig>(ctx).ForwardAsync(sig, null, ct);
        return SignaturePropertyProjector.ProjectFromOutputs(prediction);
    }

    async Task<(IReadOnlyDictionary<string, IReadOnlyList<Example>>, IReadOnlyDictionary<string, string>)>
        IOptimizableModule.RunBootstrapAsync(
            IReadOnlyList<Example> trainset,
            IProgress<OptimizerProgress>? progress,
            CancellationToken ct)
    {
        var ctxField = typeof(Predict<TSig>).GetField(
            "_ctorCtx", BindingFlags.NonPublic | BindingFlags.Instance);
        var baseCtx = (AmpelumContext?)ctxField?.GetValue(Inner)
            ?? AmpelumContext.Ambient
            ?? throw new InvalidOperationException(
                "No AmpelumContext bound to this Predict. Construct Predict<TSig> with an explicit context.");

        var optimizer = new BootstrapFewShot<Predict<TSig>>(
            async (predict, ex, c, token) =>
            {
                var sig = SignaturePropertyProjector.ProjectTo<TSig>(ex.Inputs);
                await predict.WithContext(c).ForwardAsync(sig, null, token);
            });

        var result = await optimizer.CompileAsync(Inner, trainset, new NoOpMetric(), baseCtx, progress, ct);
        return (result.DemosBySubmodule, result.InstructionOverrides);
    }

    private sealed class NoOpMetric : IMetric
    {
        public double Score(object actual, object expected, SignatureMetadata signature) => 1.0;
    }
}

using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Optimizers;

/// <summary>
/// Compiles (optimizes) a user program against labeled examples and a metric.
/// Returns an <see cref="OptimizedProgram{TProgram}"/> carrying optimized demos
/// and per-submodule instruction overrides.
/// </summary>
/// <typeparam name="TProgram">The user program type.</typeparam>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IOptimizer<TProgram>
{
    /// <summary>
    /// Compiles the given program against <paramref name="trainset"/> and <paramref name="metric"/>.
    /// Returns an optimized snapshot ready to bind into an <see cref="AmpelumContext"/>.
    /// </summary>
    Task<OptimizedProgram<TProgram>> CompileAsync(
        TProgram program,
        IReadOnlyList<Example> trainset,
        IMetric metric,
        AmpelumContext context,
        IProgress<OptimizerProgress>? progress = null,
        CancellationToken ct = default);
}

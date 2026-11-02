using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Optimizers;

namespace SignalGrid.Ampelum.Host.ModuleRegistry;

internal interface IOptimizableModule
{
    Task<(IReadOnlyDictionary<string, IReadOnlyList<Example>> DemosBySubmodule,
          IReadOnlyDictionary<string, string> InstructionOverrides)>
        RunBootstrapAsync(
            IReadOnlyList<Example> trainset,
            IProgress<OptimizerProgress>? progress,
            CancellationToken ct);
}

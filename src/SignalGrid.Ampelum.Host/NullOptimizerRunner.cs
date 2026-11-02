using SignalGrid.Ampelum.Host.Contracts;

namespace SignalGrid.Ampelum.Host;

internal sealed class NullOptimizerRunner : IOptimizerRunner
{
    public ValueTask EnqueueAsync(string runId, OptimizeRequest request, CancellationToken ct) =>
        ValueTask.CompletedTask;
}

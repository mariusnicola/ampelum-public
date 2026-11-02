using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.Host.Contracts;

namespace SignalGrid.Ampelum.Host;

/// <summary>Enqueues optimizer runs for asynchronous processing.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IOptimizerRunner
{
    /// <summary>Enqueues an optimizer run for asynchronous execution.</summary>
    ValueTask EnqueueAsync(string runId, OptimizeRequest request, CancellationToken ct);
}

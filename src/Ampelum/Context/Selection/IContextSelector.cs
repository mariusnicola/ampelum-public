using System.Threading;
using System.Threading.Tasks;
using SignalGrid.Ampelum.Context;

namespace SignalGrid.Ampelum.Context.Selection;

/// <summary>Selects events from the event store to include in the context for a turn.</summary>
public interface IContextSelector
{
    /// <summary>Produces a selection plan for the given request.</summary>
    Task<SelectionPlan> PlanAsync(IEventStore store, SelectionRequest request, CancellationToken ct);
}

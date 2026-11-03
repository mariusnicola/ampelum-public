using SignalGrid.Ampelum;

namespace SignalGrid.Ampelum.Host.ModuleRegistry;

/// <summary>Typed replay seam — implemented by adapters that wrap a Predict module.</summary>
internal interface IReplayableModule
{
    /// <summary>
    /// Re-runs forward with the provided typed inputs and a custom AmpelumContext (typically
    /// stripped of AuditHook + BudgetHook, with AuditReplayHook prepended).
    /// Returns the output field dict for wire serialisation.
    /// </summary>
    /// <summary>True when <paramref name="inputs"/> carry every required input of this module's signature.</summary>
    bool AcceptsInputs(IReadOnlyDictionary<string, object?> inputs);

    ValueTask<IReadOnlyDictionary<string, object?>> ReplayAsync(
        IReadOnlyDictionary<string, object?> inputs, AmpelumContext ctx, CancellationToken ct);
}

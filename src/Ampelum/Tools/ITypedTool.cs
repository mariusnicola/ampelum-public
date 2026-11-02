namespace SignalGrid.Ampelum.Tools;

/// <summary>
/// A deterministic, typed tool driven by an LLM via structured parameters. Spec Â§3.2.
/// The LLM never writes free-form strings unless a property/parameter is marked <see cref="FreeFormAttribute"/>.
/// </summary>
/// <typeparam name="TResult">The tool's return type.</typeparam>
public interface ITypedTool<TResult>
{
    /// <summary>Stable, machine-readable identifier shown to the LLM.</summary>
    string Name { get; }
    /// <summary>Human-readable description rendered in the LLM tool catalogue.</summary>
    string Description { get; }
    /// <summary>Invokes the tool with the typed properties already populated.</summary>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<TResult> InvokeAsync(CancellationToken ct);
}

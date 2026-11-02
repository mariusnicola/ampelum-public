namespace SignalGrid.Ampelum.Agent;

/// <summary>Implemented by tool parameter types that require an observation token.
/// MutatingTool&lt;TParams&gt; constrains TParams to this interface for type-safe validation.</summary>
public interface IHasObservationToken
{
    /// <summary>Observation token ID — validated before the tool handler executes.
    /// Null or empty means no token was provided.</summary>
    string? Token { get; }
}

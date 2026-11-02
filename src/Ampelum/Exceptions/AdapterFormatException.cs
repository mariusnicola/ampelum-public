namespace SignalGrid.Ampelum.Exceptions;

/// <summary>
/// Thrown by an <see cref="Ampelum.IAdapter"/> implementation when it cannot format a prompt
/// because the supplied inputs or outputs are missing required fields or are otherwise structurally invalid.
/// </summary>
public sealed class AdapterFormatException(string message, Exception? inner = null)
    : AmpelumException(message, inner);

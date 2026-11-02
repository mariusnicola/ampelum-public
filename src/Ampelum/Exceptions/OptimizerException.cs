namespace SignalGrid.Ampelum.Exceptions;

/// <summary>Thrown when an optimizer encounters an unrecoverable error during compilation.</summary>
public sealed class OptimizerException(string message, Exception? inner = null)
    : AmpelumException(message, inner);

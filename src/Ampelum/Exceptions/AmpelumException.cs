namespace SignalGrid.Ampelum.Exceptions;

/// <summary>Base class for all Ampelum exceptions. Catch this type to handle any library-thrown error.</summary>
public abstract class AmpelumException(string message, Exception? inner = null) : Exception(message, inner);

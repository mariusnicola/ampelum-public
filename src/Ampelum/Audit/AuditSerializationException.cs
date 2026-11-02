namespace SignalGrid.Ampelum.Audit;

/// <summary>
/// Thrown when an <see cref="AuditEntry"/>'s Inputs or Outputs contains a value that is not a
/// JSON-primitive shape (string, integral, double, bool, null, dict-of-same, list-of-same). PSQ-3.
/// </summary>
public sealed class AuditSerializationException : InvalidOperationException
{
    /// <summary>Initializes a new instance with the specified diagnostic message.</summary>
    /// <param name="message">Message describing the offending value path and type.</param>
    public AuditSerializationException(string message) : base(message) { }
}

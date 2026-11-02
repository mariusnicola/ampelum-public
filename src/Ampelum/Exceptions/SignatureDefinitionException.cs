namespace SignalGrid.Ampelum.Exceptions;

/// <summary>
/// Thrown during signature metadata resolution when a type decorated with <see cref="SignalGrid.Ampelum.SignatureAttribute"/>
/// has a structural problem â€” for example, it is not a record, or it is missing required input/output fields.
/// </summary>
public sealed class SignatureDefinitionException(string message) : AmpelumException(message);

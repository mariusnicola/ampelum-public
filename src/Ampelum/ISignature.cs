namespace SignalGrid.Ampelum;

/// <summary>
/// Marker interface for a Ampelum signature â€” an attributed record describing a program step's I/O.
/// Implement this interface on a <c>record</c> and annotate properties with
/// <see cref="InputAttribute"/> and <see cref="OutputAttribute"/> to define the field contract.
/// </summary>
public interface ISignature { }

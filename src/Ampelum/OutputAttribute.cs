namespace SignalGrid.Ampelum;

/// <summary>
/// Marks a record property as an output field of a Ampelum signature.
/// The optional <see cref="Desc"/> overrides the auto-generated human-readable field description.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, Inherited = false)]
public sealed class OutputAttribute : Attribute
{
    /// <summary>Optional human-readable description of this field shown to the LM. Auto-generated from the property name if omitted.</summary>
    public string? Desc { get; init; }
}

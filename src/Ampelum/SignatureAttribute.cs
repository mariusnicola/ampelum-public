namespace SignalGrid.Ampelum;

/// <summary>
/// Annotates a record type as a Ampelum signature and supplies the natural-language task instructions
/// that will be included in every prompt formatted by an adapter.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class SignatureAttribute(string instructions) : Attribute
{
    /// <summary>Natural-language task description inserted at the top of the adapter prompt.</summary>
    public string Instructions { get; } = instructions;
}

namespace SignalGrid.Ampelum;

/// <summary>
/// Resolved metadata for a Ampelum signature type â€” populated once by <see cref="SignatureMetadataCache"/> and
/// cached for the lifetime of the process. Contains the task instructions and ordered lists of input and output fields.
/// </summary>
/// <param name="SignatureType">The CLR <see cref="Type"/> of the signature record.</param>
/// <param name="Instructions">Natural-language task description from <see cref="SignatureAttribute"/>.</param>
/// <param name="Inputs">Ordered list of input fields declared with <see cref="InputAttribute"/>.</param>
/// <param name="Outputs">Ordered list of output fields declared with <see cref="OutputAttribute"/>.</param>
public sealed record SignatureMetadata(
    Type SignatureType,
    string Instructions,
    IReadOnlyList<Field> Inputs,
    IReadOnlyList<Field> Outputs)
{
    /// <summary><see langword="true"/> when this metadata describes a runtime-constructed (DSL-parsed)
    /// signature. Sentinel: <see cref="SignatureType"/> equals <c>typeof(Dictionary&lt;string, object?&gt;)</c>.</summary>
    public bool IsDynamic => SignatureType == typeof(Dictionary<string, object?>);

    /// <summary>
    /// Returns a new <see cref="SignatureMetadata"/> with <see cref="Instructions"/> replaced by
    /// <paramref name="newInstructions"/>. All other fields are unchanged.
    /// Uses record <c>with</c> expression; original is never mutated.
    /// </summary>
    public SignatureMetadata WithInstructions(string newInstructions) =>
        this with { Instructions = newInstructions };

    /// <summary>
    /// Returns a new <see cref="SignatureMetadata"/> where each <see cref="Field"/> whose
    /// <see cref="Field.Name"/> matches a key in <paramref name="fieldDescriptions"/> has its
    /// <see cref="Field.Description"/> replaced by the corresponding value.
    /// Fields with no matching key are unchanged. Inputs and Outputs are both scanned.
    /// Uses record <c>with</c> expressions; original is never mutated.
    /// </summary>
    public SignatureMetadata WithUpdatedFields(
        IReadOnlyDictionary<string, string> fieldDescriptions)
    {
        var updatedInputs = Inputs
            .Select(f => fieldDescriptions.TryGetValue(f.Name, out var desc)
                ? f with { Description = desc } : f)
            .ToList();
        var updatedOutputs = Outputs
            .Select(f => fieldDescriptions.TryGetValue(f.Name, out var desc)
                ? f with { Description = desc } : f)
            .ToList();
        return this with { Inputs = updatedInputs, Outputs = updatedOutputs };
    }
}

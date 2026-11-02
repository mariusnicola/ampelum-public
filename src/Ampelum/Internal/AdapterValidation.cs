using SignalGrid.Ampelum.Exceptions;

namespace SignalGrid.Ampelum.Internal;

/// <summary>
/// Shared validation helpers for adapter implementations.
/// Enforces that all required input and output fields are present in the supplied dictionaries
/// before any prompt is formatted or serialised.
/// </summary>
internal static class AdapterValidation
{
    /// <summary>
    /// Verifies that every input field declared on <paramref name="signature"/> is present in
    /// <paramref name="inputs"/>. Throws <see cref="AdapterFormatException"/> on the first missing field.
    /// </summary>
    internal static void ValidateInputs(SignatureMetadata signature, IReadOnlyDictionary<string, object?> inputs)
    {
        foreach (var f in signature.Inputs)
            if (!inputs.ContainsKey(f.Name))
                throw new AdapterFormatException(
                    $"Missing required input '{f.Name}' for signature '{signature.SignatureType.Name}'.");
    }

    /// <summary>
    /// Verifies that every output field declared on <paramref name="signature"/> is present in
    /// <paramref name="outputs"/>. Throws <see cref="AdapterFormatException"/> on the first missing field.
    /// </summary>
    internal static void ValidateOutputs(SignatureMetadata signature, IReadOnlyDictionary<string, object?> outputs)
    {
        foreach (var f in signature.Outputs)
            if (!outputs.ContainsKey(f.Name))
                throw new AdapterFormatException(
                    $"Missing required output '{f.Name}' for signature '{signature.SignatureType.Name}'.");
    }
}

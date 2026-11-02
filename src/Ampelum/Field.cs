namespace SignalGrid.Ampelum;

/// <summary>
/// Describes a single input or output field in a Ampelum signature, including its CLR type,
/// human-readable description, direction, and nullability.
/// </summary>
/// <param name="Name">Property name as declared on the signature record.</param>
/// <param name="ClrType">CLR type of the property, including nullability wrappers.</param>
/// <param name="Description">Human-readable description shown to the LM in the prompt.</param>
/// <param name="Kind">Whether this is an <see cref="FieldKind.Input"/> or <see cref="FieldKind.Output"/> field.</param>
/// <param name="IsOptional"><see langword="true"/> if the field is nullable and may be omitted from LM output.</param>
public sealed record Field(
    string Name,
    Type ClrType,
    string Description,
    FieldKind Kind,
    bool IsOptional);

using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Internal;

namespace SignalGrid.Ampelum;

/// <summary>Static factory for constructing <see cref="SignatureMetadata"/> from a compact string DSL.</summary>
public static partial class Signature
{
    /// <summary>
    /// Parses a compact signature string. Format: <c>"field1, field2 -&gt; field3, field4"</c> or
    /// <c>"Instructions. // field1 -&gt; field2"</c>. Field names become both <c>Name</c> and
    /// description (via <c>HumanizeName.Split</c>); all fields are <c>typeof(string)</c> in v0.2.
    /// </summary>
    /// <exception cref="ArgumentNullException">If <paramref name="dsl"/> is <see langword="null"/>.</exception>
    /// <exception cref="SignatureDefinitionException">On any syntax error (missing arrow, empty side, multiple arrows, empty field name).</exception>
    public static SignatureMetadata Parse(string dsl, string? instructions = null)
    {
        ArgumentNullException.ThrowIfNull(dsl);
        var trimmed = dsl.Trim();
        if (trimmed.Length == 0)
            throw new SignatureDefinitionException("Signature DSL is empty.");

        var body = trimmed;
        var effectiveInstructions = instructions ?? "";
        var sepIdx = trimmed.IndexOf("//", StringComparison.Ordinal);
        if (sepIdx >= 0)
        {
            effectiveInstructions = trimmed[..sepIdx].Trim();
            body = trimmed[(sepIdx + 2)..].Trim();
        }

        var parts = body.Split("->", StringSplitOptions.None);
        if (parts.Length != 2)
            throw new SignatureDefinitionException(
                $"Signature DSL '{dsl}' must contain exactly one '->' separator.");

        var inputs = ParseFields(parts[0], FieldKind.Input, dsl);
        var outputs = ParseFields(parts[1], FieldKind.Output, dsl);
        return new SignatureMetadata(
            typeof(Dictionary<string, object?>),
            effectiveInstructions,
            inputs,
            outputs);
    }

    static IReadOnlyList<Field> ParseFields(string side, FieldKind kind, string dsl)
    {
        var raw = side.Split(',', StringSplitOptions.None)
            .Select(s => s.Trim())
            .ToArray();
        if (raw.Any(s => s.Length == 0))
            throw new SignatureDefinitionException(
                $"Signature DSL '{dsl}' has an empty field on the {kind} side.");
        return raw
            .Select(name => new Field(name, typeof(string), HumanizeName.Split(name), kind, IsOptional: false))
            .ToArray();
    }
}

using System.Collections;

namespace SignalGrid.Ampelum.Audit;

/// <summary>
/// Validates that an audit dictionary contains only JSON-primitive shapes (PSQ-3).
/// Throws <see cref="AuditSerializationException"/> with the offending key path when a value is invalid.
/// </summary>
internal static class AuditPrimitiveValidator
{
    public static void Validate(string root, IReadOnlyDictionary<string, object?> dict)
    {
        foreach (var kv in dict)
            ValidateValue($"{root}.{kv.Key}", kv.Value);
    }

    static void ValidateValue(string path, object? value)
    {
        switch (value)
        {
            case null:
            case string:
            case bool:
            case double:
            case float:
            case sbyte: case byte:
            case short: case ushort:
            case int:   case uint:
            case long:  case ulong:
                return;
            case IReadOnlyDictionary<string, object?> nested:
                foreach (var kv in nested)
                    ValidateValue($"{path}.{kv.Key}", kv.Value);
                return;
            case IEnumerable list when value is not string:
                int i = 0;
                foreach (var item in list)
                    ValidateValue($"{path}[{i++}]", item);
                return;
            default:
                throw new AuditSerializationException(
                    $"Audit value at '{path}' is not a JSON-primitive shape (got {value.GetType().FullName}).");
        }
    }
}

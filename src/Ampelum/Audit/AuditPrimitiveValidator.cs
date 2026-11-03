using System.Collections;

namespace SignalGrid.Ampelum.Audit;

/// <summary>
/// The one definition of which values an audit dictionary may hold (PSQ-3), shared by the hasher and the
/// file sink so that whatever is hashed is also written and read back as the same text.
/// Accepted: null, string, bool, every integer type that fits in a long, float and finite double,
/// string-keyed dictionaries and any sequence (array, list, immutable array) of accepted values.
/// Anything else, including a byte array, a non-finite number or an integer above <see cref="long.MaxValue"/>,
/// throws <see cref="AuditSerializationException"/> with the offending key path.
/// </summary>
internal static class AuditPrimitiveValidator
{
    public static void Validate(string root, IReadOnlyDictionary<string, object?> dict) => CanonicalMap(root, dict);

    /// <summary>
    /// Copies <paramref name="dict"/> into the canonical form: keys sorted ordinally, values reduced to
    /// null, string, bool, long, double, <c>SortedDictionary</c> and <c>List</c>.
    /// </summary>
    public static SortedDictionary<string, object?> CanonicalMap(string path, IReadOnlyDictionary<string, object?> dict)
    {
        var sorted = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kv in dict)
            sorted[kv.Key] = Canonical($"{path}.{kv.Key}", kv.Value);
        return sorted;
    }

    static object? Canonical(string path, object? value)
    {
        switch (value)
        {
            case null:
            case string:
            case bool:
                return value;
            case sbyte: case byte:
            case short: case ushort:
            case int:   case uint:
            case long:
                return Convert.ToInt64(value);
            case ulong big:
                return big <= long.MaxValue ? (long)big : throw Rejected(path, value);
            case float single:
                return Number(path, single, value);
            case double number:
                return Number(path, number, value);
            case byte[]:
                throw Rejected(path, value);
            case IReadOnlyDictionary<string, object?> nested:
                return CanonicalMap(path, nested);
            case IDictionary typed:
                var sorted = new SortedDictionary<string, object?>(StringComparer.Ordinal);
                foreach (DictionaryEntry pair in typed)
                    sorted[pair.Key as string ?? throw Rejected(path, value)] = Canonical($"{path}.{pair.Key}", pair.Value);
                return sorted;
            case IEnumerable list:
                var items = new List<object?>();
                foreach (var item in list)
                    items.Add(Canonical($"{path}[{items.Count}]", item));
                return items;
            default:
                throw Rejected(path, value);
        }
    }

    // -0.0 and 0.0 are one value: the file turns "-0" back into the integer 0, so both hash as 0.
    static object Number(string path, double number, object original) =>
        !double.IsFinite(number) ? throw Rejected(path, original)
        : number == 0 ? (object)0L
        : number;

    static AuditSerializationException Rejected(string path, object value) =>
        new($"Audit value at '{path}' is not a JSON-primitive shape (got {value.GetType().FullName}).");
}

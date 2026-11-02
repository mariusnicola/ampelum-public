using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SignalGrid.Ampelum.Caching.Semantic;

/// <summary>
/// Internal helper that produces a deterministic SHA-256 string key from a module name +
/// input dictionary. Used by <see cref="SemanticCacheHook"/> as the cache key.
/// </summary>
internal static class SemanticCacheKey
{
    const char UnitSep = (char)0x1F;
    const char RecordSep = (char)0x1E;

    public static string Compute(string moduleName, IReadOnlyDictionary<string, object?> inputs)
    {
        var sb = new StringBuilder();
        sb.Append(moduleName).Append(UnitSep);
        foreach (var kv in inputs.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            sb.Append(kv.Key).Append(UnitSep);
            sb.Append(Format(kv.Value)).Append(RecordSep);
        }
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    static string Format(object? value) => value switch
    {
        null => "\0null",
        string s => "\0s:" + s,
        bool b => "\0b:" + (b ? "1" : "0"),
        IFormattable f => "\0n:" + f.ToString(null, CultureInfo.InvariantCulture),
        _ => "\0o:" + value.ToString(),
    };
}

using System.Reflection;

namespace SignalGrid.Ampelum.Metrics;

/// <summary>
/// Metric that scores <c>1.0</c> when the named output field is equal under
/// <see cref="StringComparison.OrdinalIgnoreCase"/>, else <c>0.0</c>. For non-string fields
/// falls back to <see cref="object.Equals(object, object)"/>.
/// </summary>
public sealed class CaseInsensitiveMatch<TSig>(string fieldName) : IMetric<TSig>
    where TSig : ISignature
{
    readonly string _fieldName = fieldName;

    /// <summary>
    /// Compares the named field between actual and expected signatures using case-insensitive
    /// comparison for strings or ordinal equality for other types.
    /// </summary>
    public double Score(TSig actual, TSig expected, SignatureMetadata signature)
    {
        var prop = typeof(TSig).GetProperty(_fieldName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new ArgumentException($"Signature '{typeof(TSig).Name}' has no property '{_fieldName}'.", nameof(fieldName));
        var a = prop.GetValue(actual);
        var e = prop.GetValue(expected);
        if (a is string sa && e is string se)
            return string.Equals(sa, se, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;
        return Equals(a, e) ? 1.0 : 0.0;
    }
}

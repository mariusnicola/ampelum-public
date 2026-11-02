using System.Reflection;

namespace SignalGrid.Ampelum.Metrics;

/// <summary>
/// Metric that scores <c>1.0</c> when the named output field (fieldName)
/// is equal (ordinal, case-sensitive) between actual and expected, else <c>0.0</c>.
/// Null equals null. Thread-safe and stateless.
/// </summary>
public sealed class ExactMatch<TSig>(string fieldName) : IMetric<TSig>, INamedMetric
    where TSig : ISignature
{
    readonly string _fieldName = fieldName;

    /// <summary>Stable identifier of the form <c>exact_match[FieldName]</c>.</summary>
    public string Name => $"exact_match[{_fieldName}]";

    /// <summary>
    /// Compares the named field between actual and expected signatures using ordinal equality.
    /// </summary>
    public double Score(TSig actual, TSig expected, SignatureMetadata signature)
    {
        var prop = typeof(TSig).GetProperty(_fieldName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new ArgumentException($"Signature '{typeof(TSig).Name}' has no property '{_fieldName}'.", nameof(fieldName));
        var a = prop.GetValue(actual);
        var e = prop.GetValue(expected);
        return Equals(a, e) ? 1.0 : 0.0;
    }
}

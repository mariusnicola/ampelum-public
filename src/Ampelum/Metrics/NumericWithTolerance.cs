using System.Reflection;

namespace SignalGrid.Ampelum.Metrics;

/// <summary>
/// Scores 1.0 when |actual-expected| â‰¤ tolerance, else 0.0. Supports int/long/double/float.
/// Throws InvalidOperationException for non-numeric or null fields. Thread-safe and stateless.
/// </summary>
public sealed class NumericWithTolerance<TSig>(string fieldName, double tolerance) : IMetric<TSig>
    where TSig : ISignature
{
    readonly string _fieldName = fieldName;
    readonly double _tolerance = tolerance;

    /// <summary>
    /// Compares the named numeric field between actual and expected using the tolerance.
    /// </summary>
    public double Score(TSig actual, TSig expected, SignatureMetadata signature)
    {
        var prop = typeof(TSig).GetProperty(_fieldName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new ArgumentException($"Signature '{typeof(TSig).Name}' has no property '{_fieldName}'.", nameof(fieldName));
        var av = prop.GetValue(actual);
        var ev = prop.GetValue(expected);
        var a = ToDouble(av, _fieldName);
        var e = ToDouble(ev, _fieldName);
        return Math.Abs(a - e) <= _tolerance ? 1.0 : 0.0;
    }

    static double ToDouble(object? v, string field) => v switch
    {
        int i      => i,
        long l     => l,
        double d   => d,
        float f    => f,
        null       => throw new InvalidOperationException($"Field '{field}' is null; cannot compute numeric tolerance."),
        _          => throw new InvalidOperationException($"Field '{field}' has non-numeric type '{v.GetType().Name}'."),
    };
}

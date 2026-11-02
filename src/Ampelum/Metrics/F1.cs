using System.Reflection;

namespace SignalGrid.Ampelum.Metrics;

/// <summary>
/// Token-level F1 over a named string output field. Tokens split on any whitespace, empty removed.
/// Both empty â†’ 1.0 (vacuous); one empty â†’ 0.0. Thread-safe and stateless.
/// </summary>
public sealed class F1<TSig>(string fieldName) : IMetric<TSig>, INamedMetric
    where TSig : ISignature
{
    readonly string _fieldName = fieldName;

    /// <summary>Stable identifier of the form <c>f1[FieldName]</c>.</summary>
    public string Name => $"f1[{_fieldName}]";

    /// <summary>
    /// Computes F1 score as the harmonic mean of precision and recall over tokenized field strings.
    /// </summary>
    public double Score(TSig actual, TSig expected, SignatureMetadata signature)
    {
        var prop = typeof(TSig).GetProperty(_fieldName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new ArgumentException($"Signature '{typeof(TSig).Name}' has no property '{_fieldName}'.", nameof(fieldName));
        var a = prop.GetValue(actual) as string ?? "";
        var e = prop.GetValue(expected) as string ?? "";
        var aT = a.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var eT = e.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (aT.Length == 0 && eT.Length == 0) return 1.0;
        if (aT.Length == 0 || eT.Length == 0) return 0.0;
        var expectedSet = new HashSet<string>(eT);
        var overlap = 0;
        foreach (var t in aT) if (expectedSet.Contains(t)) overlap++;
        if (overlap == 0) return 0.0;
        var precision = (double)overlap / aT.Length;
        var recall    = (double)overlap / eT.Length;
        return 2 * precision * recall / (precision + recall);
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace SignalGrid.Ampelum.Metrics;

/// <summary>
/// BLEU-N over the named string output field. Geometric mean of modified n-gram precisions
/// (n=1..maxN) with brevity penalty. Add-1 smoothing prevents zero collapse on missing
/// higher-order n-grams. Stateless and thread-safe.
/// </summary>
/// <remarks>
/// Default <c>maxN</c> is 4 (BLEU-4, the standard). Setting <c>maxN</c>=1 reduces to
/// unigram precision with brevity penalty. Both empty â†’ 1.0; one empty â†’ 0.0.
/// </remarks>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class Bleu<TSig> : IMetric<TSig>, INamedMetric
    where TSig : ISignature
{
    readonly int _maxN;
    readonly string _fieldName;

    /// <summary>Initialises a BLEU-N metric with the given maximum n-gram order.</summary>
    /// <param name="maxN">Maximum n-gram order (positive; typical default 4).</param>
    /// <param name="fieldName">Name of the string field on <typeparamref name="TSig"/> to score.</param>
    public Bleu(int maxN, string fieldName)
    {
        if (maxN <= 0) throw new ArgumentOutOfRangeException(nameof(maxN), "maxN must be positive.");
        ArgumentException.ThrowIfNullOrEmpty(fieldName);
        _maxN = maxN;
        _fieldName = fieldName;
    }

    /// <summary>Stable identifier of the form <c>bleu-N[FieldName]</c>.</summary>
    public string Name => $"bleu-{_maxN}[{_fieldName}]";

    /// <inheritdoc/>
    public double Score(TSig actual, TSig expected, SignatureMetadata signature)
    {
        var prop = typeof(TSig).GetProperty(_fieldName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new ArgumentException($"Signature '{typeof(TSig).Name}' has no property '{_fieldName}'.", nameof(signature));
        var c = (prop.GetValue(actual) as string ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var r = (prop.GetValue(expected) as string ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (c.Length == 0 && r.Length == 0) return 1.0;
        if (c.Length == 0 || r.Length == 0) return 0.0;

        double logSum = 0;
        for (var n = 1; n <= _maxN; n++)
        {
            if (c.Length < n) return 0.0;
            var (matches, total) = CountModifiedMatches(c, r, n);
            // Add-1 smoothing
            var p = (matches + 1.0) / (total + 1.0);
            logSum += Math.Log(p);
        }
        var geoMean = Math.Exp(logSum / _maxN);
        var bp = c.Length >= r.Length ? 1.0 : Math.Exp(1.0 - (double)r.Length / c.Length);
        return bp * geoMean;
    }

    static (int matches, int total) CountModifiedMatches(string[] candidate, string[] reference, int n)
    {
        var refCounts = new Dictionary<string, int>();
        for (var i = 0; i + n <= reference.Length; i++)
        {
            var g = string.Join(' ', reference, i, n);
            refCounts[g] = refCounts.TryGetValue(g, out var v) ? v + 1 : 1;
        }
        var matches = 0;
        var total = 0;
        for (var i = 0; i + n <= candidate.Length; i++)
        {
            var g = string.Join(' ', candidate, i, n);
            total++;
            if (refCounts.TryGetValue(g, out var v) && v > 0) { matches++; refCounts[g] = v - 1; }
        }
        return (matches, total);
    }
}

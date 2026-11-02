using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace SignalGrid.Ampelum.Metrics;

/// <summary>
/// ROUGE-N: recall of overlapping n-grams between the named string output field of
/// <typeparamref name="TSig"/> and the reference (expected). Score in <c>[0,1]</c>.
/// Both empty â†’ 1.0 (vacuous match); one empty â†’ 0.0. Stateless and thread-safe.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class RougeN<TSig> : IMetric<TSig>, INamedMetric
    where TSig : ISignature
{
    readonly int _n;
    readonly string _fieldName;

    /// <summary>Initialises a ROUGE-N metric with the given n-gram order and signature field name.</summary>
    /// <param name="n">N-gram order (must be positive).</param>
    /// <param name="fieldName">Name of the string field on <typeparamref name="TSig"/> to score.</param>
    public RougeN(int n, string fieldName)
    {
        if (n <= 0) throw new ArgumentOutOfRangeException(nameof(n), "n must be positive.");
        ArgumentException.ThrowIfNullOrEmpty(fieldName);
        _n = n;
        _fieldName = fieldName;
    }

    /// <summary>Stable identifier of the form <c>rouge-N[FieldName]</c>.</summary>
    public string Name => $"rouge-{_n}[{_fieldName}]";

    /// <inheritdoc/>
    public double Score(TSig actual, TSig expected, SignatureMetadata signature)
    {
        var prop = typeof(TSig).GetProperty(_fieldName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new ArgumentException($"Signature '{typeof(TSig).Name}' has no property '{_fieldName}'.", nameof(signature));
        var a = (prop.GetValue(actual) as string ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var e = (prop.GetValue(expected) as string ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (a.Length == 0 && e.Length == 0) return 1.0;
        if (a.Length == 0 || e.Length == 0) return 0.0;
        var aGrams = NGrams(a, _n);
        var eGrams = NGrams(e, _n);
        if (eGrams.Count == 0) return 0.0;
        var overlap = CountOverlap(aGrams, eGrams);
        return (double)overlap / eGrams.Count;
    }

    static List<string> NGrams(string[] tokens, int n)
    {
        var list = new List<string>(Math.Max(0, tokens.Length - n + 1));
        for (var i = 0; i + n <= tokens.Length; i++) list.Add(string.Join(' ', tokens, i, n));
        return list;
    }

    static int CountOverlap(List<string> candidate, List<string> reference)
    {
        var refCounts = new Dictionary<string, int>();
        foreach (var g in reference) refCounts[g] = refCounts.TryGetValue(g, out var c) ? c + 1 : 1;
        var overlap = 0;
        foreach (var g in candidate)
            if (refCounts.TryGetValue(g, out var c) && c > 0) { overlap++; refCounts[g] = c - 1; }
        return overlap;
    }
}

/// <summary>
/// ROUGE-L: F-measure based on Longest Common Subsequence between candidate and reference token lists.
/// Uses Î²=1.2 per Lin 2004 (favors recall slightly). Stateless and thread-safe.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class RougeL<TSig> : IMetric<TSig>, INamedMetric
    where TSig : ISignature
{
    const double Beta = 1.2;
    readonly string _fieldName;

    /// <summary>Initialises a ROUGE-L metric over the named signature field.</summary>
    /// <param name="fieldName">Name of the string field on <typeparamref name="TSig"/> to score.</param>
    public RougeL(string fieldName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fieldName);
        _fieldName = fieldName;
    }

    /// <summary>Stable identifier of the form <c>rouge-l[FieldName]</c>.</summary>
    public string Name => $"rouge-l[{_fieldName}]";

    /// <inheritdoc/>
    public double Score(TSig actual, TSig expected, SignatureMetadata signature)
    {
        var prop = typeof(TSig).GetProperty(_fieldName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new ArgumentException($"Signature '{typeof(TSig).Name}' has no property '{_fieldName}'.", nameof(signature));
        var a = (prop.GetValue(actual) as string ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var e = (prop.GetValue(expected) as string ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (a.Length == 0 && e.Length == 0) return 1.0;
        if (a.Length == 0 || e.Length == 0) return 0.0;
        var lcs = Lcs(a, e);
        if (lcs == 0) return 0.0;
        var p = (double)lcs / a.Length;
        var r = (double)lcs / e.Length;
        var b2 = Beta * Beta;
        return (1 + b2) * p * r / (r + b2 * p);
    }

    static int Lcs(string[] x, string[] y)
    {
        var m = x.Length; var n = y.Length;
        var prev = new int[n + 1];
        var curr = new int[n + 1];
        for (var i = 1; i <= m; i++)
        {
            for (var j = 1; j <= n; j++)
            {
                curr[j] = string.Equals(x[i - 1], y[j - 1], StringComparison.Ordinal)
                    ? prev[j - 1] + 1
                    : Math.Max(prev[j], curr[j - 1]);
            }
            (prev, curr) = (curr, prev);
            Array.Clear(curr);
        }
        return prev[n];
    }
}

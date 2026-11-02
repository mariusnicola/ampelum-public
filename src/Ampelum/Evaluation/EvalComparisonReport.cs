using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SignalGrid.Ampelum.Evaluation;

/// <summary>A/B markdown report with paired-t (Nâ‰¥30) or bootstrap CI fallback (N&lt;30).</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public static class EvalComparisonReport
{
    /// <summary>
    /// Compares two evaluation runs on the same examples (paired). Returns (markdown, json).
    /// Emits a verdict: A wins / B wins / Î” ns (not significant at Î±=0.05 two-sided).
    /// </summary>
    /// <param name="labelA">Display label for run A.</param>
    /// <param name="a">Evaluation result A.</param>
    /// <param name="labelB">Display label for run B.</param>
    /// <param name="b">Evaluation result B.</param>
    /// <param name="bootstrapIterations">Iterations for the bootstrap fallback (N&lt;30). Default 2000.</param>
    /// <param name="randomSeed">Optional bootstrap seed for reproducible reports.</param>
    public static (string Markdown, string Json) Render<TSig>(
        string labelA, EvaluationResult<TSig> a,
        string labelB, EvaluationResult<TSig> b,
        int bootstrapIterations = 2000,
        int? randomSeed = null)
        where TSig : ISignature
    {
        ArgumentException.ThrowIfNullOrEmpty(labelA);
        ArgumentException.ThrowIfNullOrEmpty(labelB);
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Scores.Count != b.Scores.Count)
            throw new ArgumentException("A and B must score the same number of examples (paired comparison).");

        var n = a.Scores.Count;
        var diffs = new double[n];
        for (var i = 0; i < n; i++) diffs[i] = b.Scores[i].Score - a.Scores[i].Score;

        string verdict; string detail;
        if (n >= 30)
        {
            var (t, p) = PairedT(diffs);
            verdict = p < 0.05 ? (Mean(diffs) > 0 ? $"{labelB} wins" : $"{labelA} wins") : "delta ns";
            detail = string.Create(CultureInfo.InvariantCulture, $"paired-t t={t:0.000}, p={p:0.0000}");
        }
        else
        {
            var (lo, hi) = Bootstrap(diffs, bootstrapIterations, randomSeed);
            verdict = (lo > 0 || hi < 0) ? (Mean(diffs) > 0 ? $"{labelB} wins" : $"{labelA} wins") : "delta ns";
            detail = string.Create(CultureInfo.InvariantCulture, $"bootstrap 95% CI [{lo:0.000}, {hi:0.000}] ({bootstrapIterations} iters)");
        }

        var md = new StringBuilder();
        md.AppendLine("# A/B Evaluation Comparison");
        md.AppendLine();
        md.AppendLine("| Metric | " + labelA + " mean | " + labelB + " mean | delta | Verdict | Detail |");
        md.AppendLine("|---|---|---|---|---|---|");
        md.AppendLine(CultureInfo.InvariantCulture, $"| score | {a.Mean:0.0000} | {b.Mean:0.0000} | {(b.Mean - a.Mean):+0.0000;-0.0000} | {verdict} | {detail} |");

        var json = JsonSerializer.Serialize(new
        {
            labelA,
            labelB,
            n,
            meanA = a.Mean,
            meanB = b.Mean,
            delta = b.Mean - a.Mean,
            verdict,
            detail,
        });
        return (md.ToString(), json);
    }

    static double Mean(double[] xs) { double s = 0; foreach (var x in xs) s += x; return xs.Length == 0 ? 0 : s / xs.Length; }

    static (double t, double p) PairedT(double[] diffs)
    {
        var n = diffs.Length;
        var mean = Mean(diffs);
        double var2 = 0;
        foreach (var d in diffs) var2 += (d - mean) * (d - mean);
        var2 /= (n - 1);
        var se = Math.Sqrt(var2 / n);
        if (se == 0) return (0, 1);
        var t = mean / se;
        var p = 2 * (1 - Phi(Math.Abs(t)));
        return (t, p);
    }

    // Abramowitz-Stegun 26.2.17 â€” adequate for nâ‰¥30.
    static double Phi(double x)
    {
        var a1 = 0.254829592; var a2 = -0.284496736; var a3 = 1.421413741;
        var a4 = -1.453152027; var a5 = 1.061405429; var p = 0.3275911;
        var sign = x < 0 ? -1 : 1; x = Math.Abs(x) / Math.Sqrt(2);
        var t = 1.0 / (1.0 + p * x);
        var y = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-x * x);
        return 0.5 * (1.0 + sign * y);
    }

    static (double lo, double hi) Bootstrap(double[] diffs, int iters, int? seed)
    {
        var rng = seed is { } s ? new Random(s) : new Random();
        var n = diffs.Length;
        var means = new double[iters];
        for (var k = 0; k < iters; k++)
        {
            double sum = 0;
            for (var i = 0; i < n; i++) sum += diffs[rng.Next(n)];
            means[k] = sum / n;
        }
        Array.Sort(means);
        var lo = means[(int)(0.025 * iters)];
        var hi = means[(int)(0.975 * iters)];
        return (lo, hi);
    }
}

namespace SignalGrid.Ampelum.Cli.Reporting;

/// <summary>String metrics for ampelum eval (exact match + token-overlap F1).</summary>
internal static class SimpleMetric
{
    public static double Score(string metric, string expected, string predicted)
    {
        return metric.Equals("f1", StringComparison.OrdinalIgnoreCase)
            ? F1(expected, predicted)
            : (string.Equals(expected, predicted, StringComparison.Ordinal) ? 1d : 0d);
    }

    private static double F1(string expected, string predicted)
    {
        var e = expected.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var p = predicted.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (e.Length == 0 || p.Length == 0)
        {
            return 0d;
        }

        var eSet = new HashSet<string>(e, StringComparer.OrdinalIgnoreCase);
        var pSet = new HashSet<string>(p, StringComparer.OrdinalIgnoreCase);
        var common = eSet.Intersect(pSet, StringComparer.OrdinalIgnoreCase).Count();
        if (common == 0)
        {
            return 0d;
        }
        var prec = (double)common / pSet.Count;
        var rec = (double)common / eSet.Count;
        return 2 * prec * rec / (prec + rec);
    }
}

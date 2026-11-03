namespace SignalGrid.Ampelum.Optimizers;

/// <summary>
/// Seeded Tree-structured Parzen Estimator over independent categorical parameters.
/// The first trials are uniform random. After that the finished trials are split into a
/// good group (top quarter by score, at least one trial) and the rest. For each parameter the
/// sampler draws candidate categories from the good-group distribution l and keeps the one that
/// maximises l(x)/g(x), where g is the distribution of the rest. Both distributions use add-one
/// smoothing so no category ever has probability zero.
/// </summary>
internal sealed class TpeSampler
{
    const int DrawsPerParameter = 24;

    readonly int[] _categoryCounts;
    readonly int _randomTrials;
    readonly Random _rng;

    /// <param name="categoryCounts">Number of categories for each parameter.</param>
    /// <param name="randomTrials">Number of finished trials below which suggestions are uniform random.</param>
    /// <param name="seed">Seed of the sampler's random stream.</param>
    public TpeSampler(IReadOnlyList<int> categoryCounts, int randomTrials, int seed)
    {
        _categoryCounts = [.. categoryCounts];
        _randomTrials = randomTrials;
        _rng = new Random(seed);
    }

    /// <summary>Suggests the next choice, one category index per parameter.</summary>
    /// <param name="finished">Every finished trial: its choice and its score.</param>
    public int[] Suggest(IReadOnlyList<(int[] Choice, double Score)> finished)
    {
        if (finished.Count < _randomTrials)
            return [.. _categoryCounts.Select(n => _rng.Next(n))];

        var ordered = finished.OrderByDescending(t => t.Score).ToList();
        int goodCount = Math.Max(1, ordered.Count / 4);
        int restCount = ordered.Count - goodCount;

        var choice = new int[_categoryCounts.Length];
        for (int p = 0; p < choice.Length; p++)
        {
            int k = _categoryCounts[p];
            var goodHits = new int[k];
            var restHits = new int[k];
            for (int i = 0; i < ordered.Count; i++)
            {
                if (i < goodCount) goodHits[ordered[i].Choice[p]]++;
                else restHits[ordered[i].Choice[p]]++;
            }

            var l = new double[k];
            var g = new double[k];
            for (int c = 0; c < k; c++)
            {
                l[c] = (goodHits[c] + 1.0) / (goodCount + k);
                g[c] = (restHits[c] + 1.0) / (restCount + k);
            }

            int best = Draw(l);
            for (int d = 1; d < DrawsPerParameter; d++)
            {
                int candidate = Draw(l);
                if (l[candidate] / g[candidate] > l[best] / g[best])
                    best = candidate;
            }
            choice[p] = best;
        }
        return choice;
    }

    // l sums to one, so a uniform draw walks the cumulative weights to a category.
    int Draw(double[] weights)
    {
        double u = _rng.NextDouble();
        int c = 0;
        double acc = weights[0];
        while (acc <= u && c < weights.Length - 1)
        {
            c++;
            acc += weights[c];
        }
        return c;
    }
}

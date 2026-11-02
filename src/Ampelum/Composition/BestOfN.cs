using System.Diagnostics;
using SignalGrid.Ampelum.Metrics;

namespace SignalGrid.Ampelum.Composition;

/// <summary>
/// Generates N completions concurrently and returns the one with the highest metric score.
/// Ties: lowest index wins. Makes N LM calls per <see cref="ForwardAsync"/>.
/// </summary>
public sealed class BestOfN<TSig> : IModule<TSig, TSig> where TSig : ISignature
{
    readonly AmpelumContext _ctx;
    readonly IMetric<TSig> _metric;
    readonly int _n;
    readonly SignatureMetadata _meta;

    /// <summary>
    /// Initialises a <see cref="BestOfN{TSig}"/> module.
    /// </summary>
    /// <param name="context">Runtime context supplying the LM client, adapter, trace sink, and logging.</param>
    /// <param name="metric">Metric function to score each candidate prediction.</param>
    /// <param name="n">Number of candidates to generate in parallel. Must be >= 2. Defaults to 3.</param>
    public BestOfN(AmpelumContext context, IMetric<TSig> metric, int n = 3)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(metric);
        if (n < 2) throw new ArgumentOutOfRangeException(nameof(n), "must be >= 2");
        _ctx = context;
        _metric = metric;
        _n = n;
        _meta = SignatureMetadataCache.For<TSig>();
    }

    /// <summary>
    /// Runs N predictions in parallel, scores each via the metric, and returns the highest-scoring result.
    /// If scores are tied, the lowest index wins. Emits one <see cref="TraceRecord"/> with <c>ModuleName = "BestOfN"</c>
    /// and <c>Metadata["candidate_scores"]</c> containing all scores.
    /// </summary>
    /// <param name="inputs">Input signature to pass to each prediction.</param>
    /// <param name="demos">Optional in-context examples to pass to each prediction.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A prediction wrapping the highest-scoring candidate.</returns>
    public async Task<Prediction<TSig>> ForwardAsync(
        TSig inputs,
        IReadOnlyList<Example>? demos = null,
        CancellationToken ct = default)
    {
        using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.module.forward");
        activity?.SetTag("SignalGrid.Ampelum.module", "BestOfN");
        activity?.SetTag("SignalGrid.Ampelum.signature_type", _meta.SignatureType.Name);
        activity?.SetTag("module.n_candidates", _n);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        var predict = new Predict<TSig>(_ctx);
        var tasks = Enumerable.Range(0, _n)
            .Select(_ => predict.ForwardAsync(inputs, demos, ct))
            .ToArray();

        Prediction<TSig>[] results;
        try
        {
            results = await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // Surface the first task's exception (deterministic, order-preserving).
            var firstFaulted = tasks.FirstOrDefault(t => t.IsFaulted);
            if (firstFaulted is not null && firstFaulted.Exception is not null)
                throw firstFaulted.Exception.InnerException ?? firstFaulted.Exception;
            throw;
        }

        var scores = new double[results.Length];
        int bestIdx = 0;
        double bestScore = double.NegativeInfinity;
        for (var i = 0; i < results.Length; i++)
        {
            scores[i] = _metric.Score(results[i].Value, inputs, _meta);
            if (scores[i] > bestScore)
            {
                bestScore = scores[i];
                bestIdx = i;
            }
        }

        sw.Stop();

        var trace = new TraceRecord(
            Id: Guid.NewGuid(),
            ModuleName: "BestOfN",
            SignatureType: _meta.SignatureType,
            Inputs: new Dictionary<string, object?>(),
            Outputs: new Dictionary<string, object?>(),
            PromptMessages: Array.Empty<Microsoft.Extensions.AI.ChatMessage>(),
            RawResponse: "",
            Usage: null,
            Duration: sw.Elapsed,
            Error: null,
            Metadata: new Dictionary<string, object?> { ["candidate_scores"] = (IReadOnlyList<double>)scores });
        try { _ctx.TraceSink.Write(trace); } catch { }

        return new Prediction<TSig>(results[bestIdx].Value, trace);
    }

    [System.Diagnostics.CodeAnalysis.Experimental("AMPELUM_V09_PREVIEW")]
    Task<Prediction<TSig>> IModule<TSig, TSig>.ForwardAsync(TSig inputs, CancellationToken ct)
        => ForwardAsync(inputs, demos: null, ct);
}

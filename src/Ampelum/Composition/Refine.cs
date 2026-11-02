using System.Diagnostics;
using SignalGrid.Ampelum.Metrics;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Composition;

/// <summary>
/// Runs <see cref="Predict{TSig}"/>, scores via the metric, and if below
/// the threshold, refines up to <c>maxRefinements</c> additional times.
/// Returns the best-scoring attempt. Makes at most <c>maxRefinements + 1</c> LM calls.
/// </summary>
public sealed class Refine<TSig> : IModule<TSig, TSig> where TSig : ISignature
{
    readonly AmpelumContext _ctx;
    readonly IMetric<TSig> _metric;
    readonly double _threshold;
    readonly int _maxRefinements;
    readonly SignatureMetadata _meta;

    /// <summary>
    /// Initialises a <see cref="Refine{TSig}"/> module.
    /// </summary>
    /// <param name="context">Runtime context supplying the LM client, adapter, trace sink, and logging.</param>
    /// <param name="metric">Metric function to score each candidate prediction.</param>
    /// <param name="threshold">Score threshold at or above which refinement stops early. Defaults to 0.8.</param>
    /// <param name="maxRefinements">Maximum number of additional refinement attempts. Defaults to 2.</param>
    public Refine(AmpelumContext context, IMetric<TSig> metric, double threshold = 0.8, int maxRefinements = 2)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(metric);
        _ctx = context;
        _metric = metric;
        _threshold = threshold;
        _maxRefinements = maxRefinements;
        _meta = SignatureMetadataCache.For<TSig>();
    }

    /// <summary>
    /// Runs up to <c>maxRefinements + 1</c> predictions, scoring each via the metric.
    /// Stops early when <c>score &gt;= threshold</c>. Returns the best-scoring result.
    /// Emits one <see cref="TraceRecord"/> with <c>ModuleName = "Refine"</c>,
    /// <c>Metadata["refinement_count"]</c>, and <c>Metadata["final_score"]</c>.
    /// </summary>
    /// <param name="inputs">Input signature to pass to each prediction.</param>
    /// <param name="demos">Optional in-context examples to pass to each prediction.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A prediction wrapping the best-scoring candidate with a Refine-level trace.</returns>
    public async Task<Prediction<TSig>> ForwardAsync(
        TSig inputs,
        IReadOnlyList<Example>? demos = null,
        CancellationToken ct = default)
    {
        using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.module.forward");
        activity?.SetTag("SignalGrid.Ampelum.module", "Refine");
        activity?.SetTag("SignalGrid.Ampelum.signature_type", _meta.SignatureType.Name);
        activity?.SetTag("module.threshold", _threshold);

        var sw = Stopwatch.StartNew();
        var ctxUsed = _ctx;
        Prediction<TSig>? best = null;
        double bestScore = double.NegativeInfinity;
        int refinements = 0;

        for (var attempt = 0; attempt <= _maxRefinements; attempt++)
        {
            var predict = new Predict<TSig>(ctxUsed);
            var pred = await predict.ForwardAsync(inputs, demos, ct).ConfigureAwait(false);
            var score = _metric.Score(pred.Value, inputs, _meta);
            if (score > bestScore) { bestScore = score; best = pred; }
            if (score >= _threshold) break;
            if (attempt == _maxRefinements) break;
            refinements = attempt + 1;

            // Build refinement context by prepending a system message.
            var feedback = _metric is IRefinableFeedback fb
                ? fb.GetFeedback(pred.Value!, inputs, _meta)
                : _metric.GetType().Name;
            var refinementMsg = $"The previous answer scored {score:F2} out of 1.0. Improve it by addressing: {feedback}.";
            var baseOpts = _ctx.DefaultChatOptions?.Clone() ?? new ChatOptions();
            baseOpts.AdditionalProperties ??= new Microsoft.Extensions.AI.AdditionalPropertiesDictionary();
            // The cleanest way to prepend a system instruction is via a scoped wrapper on AmpelumContext
            // that injects a SystemMessage in Adapter.Format. Simplest cross-cutting: use a decorated adapter.
            ctxUsed = _ctx with { Adapter = new PrependingSystemAdapter(_ctx.Adapter, refinementMsg) };
        }

        sw.Stop();

        var trace = new TraceRecord(
            Id: Guid.NewGuid(),
            ModuleName: "Refine",
            SignatureType: _meta.SignatureType,
            Inputs: new Dictionary<string, object?>(),
            Outputs: new Dictionary<string, object?>(),
            PromptMessages: Array.Empty<ChatMessage>(),
            RawResponse: "",
            Usage: null,
            Duration: sw.Elapsed,
            Error: null,
            Metadata: new Dictionary<string, object?>
            {
                ["refinement_count"] = refinements,
                ["final_score"] = bestScore,
            });
        try { _ctx.TraceSink.Write(trace); } catch { }

        return best! with { Trace = trace };
    }

    sealed class PrependingSystemAdapter(IAdapter inner, string systemMessage) : IAdapter
    {
        public IReadOnlyList<ChatMessage> Format(SignatureMetadata signature,
            IReadOnlyDictionary<string, object?> inputs, IReadOnlyList<Example> demos)
        {
            var inner2 = inner.Format(signature, inputs, demos).ToList();
            inner2.Insert(0, new ChatMessage(ChatRole.System, systemMessage));
            return inner2;
        }

        public IReadOnlyDictionary<string, object?> Parse(SignatureMetadata signature, ChatResponse response) =>
            inner.Parse(signature, response);
    }

    [System.Diagnostics.CodeAnalysis.Experimental("AMPELUM_V09_PREVIEW")]
    Task<Prediction<TSig>> IModule<TSig, TSig>.ForwardAsync(TSig inputs, CancellationToken ct)
        => ForwardAsync(inputs, demos: null, ct);
}

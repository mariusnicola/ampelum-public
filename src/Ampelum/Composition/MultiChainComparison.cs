using System.Diagnostics;
using System.Reflection;
using System.Text;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Internal;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Composition;

/// <summary>Strategy for combining N candidate outputs into the final answer.</summary>
public enum AggregatorStrategy
{
    /// <summary>Pick the output that appears most often (ordinal string equality on each output field).</summary>
    MajorityVote,
    /// <summary>Meta-prompt asks the model to select the single best chain.</summary>
    ModelSelect,
    /// <summary>Meta-prompt asks the model to synthesise all chains into one answer.</summary>
    ModelSynthesize,
}

/// <summary>
/// Samples N chains-of-thought, then aggregates into a single best answer.
/// Makes <c>N + 1</c> LM calls per <see cref="ForwardAsync"/>.
/// </summary>
public sealed class MultiChainComparison<TSig> : IModule<TSig, TSig> where TSig : ISignature
{
    readonly AmpelumContext _ctx;
    readonly int _n;
    readonly AggregatorStrategy _agg;
    readonly SignatureMetadata _meta;

    /// <summary>
    /// Initialises a <see cref="MultiChainComparison{TSig}"/> module.
    /// </summary>
    /// <param name="context">Runtime context supplying the LM client, adapter, trace sink, and logging.</param>
    /// <param name="n">Number of independent chains-of-thought to sample. Must be &gt;= 2.</param>
    /// <param name="aggregator">Strategy for combining the N candidates into the final answer.</param>
    public MultiChainComparison(AmpelumContext context, int n = 3, AggregatorStrategy aggregator = AggregatorStrategy.ModelSynthesize)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (n < 2) throw new ArgumentOutOfRangeException(nameof(n), "must be >= 2");
        _ctx = context;
        _n = n;
        _agg = aggregator;
        _meta = SignatureMetadataCache.For<TSig>();
    }

    /// <summary>
    /// Runs N chains-of-thought concurrently, then aggregates into a single prediction.
    /// Emits one <see cref="TraceRecord"/> with <c>ModuleName = "MultiChainComparison"</c>
    /// and <c>Metadata["sub_traces"]</c> containing the N inner CoT <see cref="TraceRecord"/> instances.
    /// </summary>
    /// <param name="inputs">Populated signature record with all required input fields set.</param>
    /// <param name="demos">Optional few-shot demonstrations passed to each inner CoT call.</param>
    /// <param name="ct">Cancellation token; cancellation propagates through <c>Task.WhenAll</c> and individual calls.</param>
    /// <returns>A <see cref="Prediction{TSig}"/> containing the aggregated output and the outer trace.</returns>
    public async Task<Prediction<TSig>> ForwardAsync(
        TSig inputs,
        IReadOnlyList<Example>? demos = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ct.ThrowIfCancellationRequested();

        using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.module.forward");
        activity?.SetTag("SignalGrid.Ampelum.module", "MultiChainComparison");
        activity?.SetTag("SignalGrid.Ampelum.signature_type", _meta.SignatureType.Name);
        activity?.SetTag("module.n_chains", _n);
        activity?.SetTag("module.aggregator", _agg.ToString());

        var sw = Stopwatch.StartNew();

        // 1. N concurrent CoT calls.
        var tasks = Enumerable.Range(0, _n)
            .Select(_ => new ChainOfThought<TSig>(_ctx).ForwardAsync(inputs, demos, ct))
            .ToArray();
        var chainResults = await Task.WhenAll(tasks).ConfigureAwait(false);

        // 2. Aggregate.
        TSig finalValue;
        IReadOnlyList<ChatMessage> aggMessages;
        string aggRawResponse;
        if (_agg == AggregatorStrategy.MajorityVote)
        {
            finalValue = MajorityVote(chainResults);
            // Still perform an aggregation call for trace consistency with ModelSelect/ModelSynthesize.
            (aggMessages, aggRawResponse) = await RunAggregatorCall(chainResults, ct).ConfigureAwait(false);
        }
        else
        {
            (finalValue, aggMessages, aggRawResponse) = await RunModelAggregator(chainResults, inputs, ct).ConfigureAwait(false);
        }

        sw.Stop();

        var trace = new TraceRecord(
            Id: Guid.NewGuid(),
            ModuleName: "MultiChainComparison",
            SignatureType: _meta.SignatureType,
            Inputs: new Dictionary<string, object?>(),
            Outputs: new Dictionary<string, object?>(),
            PromptMessages: aggMessages,
            RawResponse: aggRawResponse,
            Usage: null,
            Duration: sw.Elapsed,
            Error: null,
            Metadata: new Dictionary<string, object?>
            {
                ["sub_traces"] = chainResults.Select(r => r.Trace).ToArray(),
            });
        try { _ctx.TraceSink.Write(trace); } catch { /* ignore */ }

        Diagnostics.ModuleForwardDuration.Record(sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("SignalGrid.Ampelum.module", "MultiChainComparison"),
            new KeyValuePair<string, object?>("SignalGrid.Ampelum.signature_type", _meta.SignatureType.Name));

        return new Prediction<TSig>(finalValue, trace);
    }

    TSig MajorityVote(Prediction<TSig>[] candidates)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in candidates)
        {
            var key = RenderOutputKey(c.Value);
            counts[key] = counts.TryGetValue(key, out var v) ? v + 1 : 1;
        }
        var winnerKey = counts.OrderByDescending(kv => kv.Value).First().Key;
        return candidates.First(c => RenderOutputKey(c.Value) == winnerKey).Value;
    }

    string RenderOutputKey(TSig v)
    {
        var sb = new StringBuilder();
        foreach (var f in _meta.Outputs)
        {
            var prop = typeof(TSig).GetProperty(f.Name, BindingFlags.Public | BindingFlags.Instance)!;
            sb.Append(f.Name).Append('=').Append(prop.GetValue(v)).Append('|');
        }
        return sb.ToString();
    }

    async Task<(IReadOnlyList<ChatMessage>, string)> RunAggregatorCall(Prediction<TSig>[] candidates, CancellationToken ct)
    {
        var prompt = BuildAggregationPrompt(candidates, instruction: "Select the best answer");
        var msgs = new[] { new ChatMessage(ChatRole.User, prompt) };
        var response = await HookedLmCall.InvokeAsync(_ctx, "MultiChainComparison.Aggregate", msgs, _ctx.DefaultChatOptions, ct).ConfigureAwait(false);
        return (msgs, $"{response.Text}");
    }

    async Task<(TSig, IReadOnlyList<ChatMessage>, string)> RunModelAggregator(Prediction<TSig>[] candidates, TSig inputs, CancellationToken ct)
    {
        var instr = _agg == AggregatorStrategy.ModelSelect
            ? "Select the single best answer from the candidates below."
            : "Synthesise a single best answer by reconciling the candidates below.";
        var prompt = BuildAggregationPrompt(candidates, instr);
        var inputsDict = new Dictionary<string, object?>();
        foreach (var f in _meta.Inputs)
            inputsDict[f.Name] = typeof(TSig).GetProperty(f.Name)!.GetValue(inputs);
        var msgs = _ctx.Adapter.Format(_meta, inputsDict, Array.Empty<Example>()).ToList();
        msgs.Insert(0, new ChatMessage(ChatRole.System, prompt));
        var response = await HookedLmCall.InvokeAsync(_ctx, "MultiChainComparison.Aggregate", msgs, _ctx.DefaultChatOptions, ct).ConfigureAwait(false);
        var parsed = _ctx.Adapter.Parse(_meta, response);
        var value = RecordCloner.CloneWith(inputs, parsed);
        return (value, msgs, $"{response.Text}");
    }

    string BuildAggregationPrompt(Prediction<TSig>[] candidates, string instruction)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are given {candidates.Length} candidate answers to the following question. {instruction}");
        for (var i = 0; i < candidates.Length; i++)
        {
            sb.AppendLine();
            sb.Append("### Candidate ").Append(i + 1).AppendLine();
            foreach (var f in _meta.Outputs)
            {
                var prop = typeof(TSig).GetProperty(f.Name)!;
                sb.Append(f.Name).Append(": ").AppendLine($"{prop.GetValue(candidates[i].Value)}");
            }
        }
        return sb.ToString();
    }

    [System.Diagnostics.CodeAnalysis.Experimental("AMPELUM_V09_PREVIEW")]
    Task<Prediction<TSig>> IModule<TSig, TSig>.ForwardAsync(TSig inputs, CancellationToken ct)
        => ForwardAsync(inputs, demos: null, ct);
}

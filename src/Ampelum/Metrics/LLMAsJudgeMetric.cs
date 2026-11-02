using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Hooks;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Metrics;

/// <summary>Aggregation strategies for multi-sample LLM-as-judge scoring.</summary>
public enum JudgeAggregation
{
    /// <summary>Median of the per-sample scores; linear interpolation for even N.</summary>
    Median,

    /// <summary>Bucket each sample to {0, 0.5, 1} and return the modal bucket; ties prefer higher.</summary>
    MajorityVote,

    /// <summary>Arithmetic mean of the per-sample scores.</summary>
    Mean,
}

/// <summary>Options for <see cref="LLMAsJudgeMetric{TSig}"/>. Multi-sample variance reduction.</summary>
/// <param name="Samples">Number of independent judge calls per Score (>= 1).</param>
/// <param name="Aggregation">How to reduce sampled scores to one.</param>
/// <param name="Criteria">Optional rendered criteria list.</param>
/// <param name="TraceSink">Optional sink to receive a TraceRecord per judge call.</param>
public sealed record LLMAsJudgeOptions(
    int Samples = 1,
    JudgeAggregation Aggregation = JudgeAggregation.Median,
    string[]? Criteria = null,
    ITraceSink? TraceSink = null);

/// <summary>
/// Uses a judge <see cref="IChatClient"/> to score predictions via a structured JSON judge prompt.
/// Stateless and thread-safe. One LM call per <see cref="Score"/> when <c>Samples == 1</c>; <c>Samples</c>
/// parallel calls otherwise (variance-reduction knob).
/// </summary>
/// <remarks>
/// <b>Sync-over-async note:</b> <see cref="IMetric{TSig}.Score"/> is synchronous; the judge call(s) are
/// awaited via <c>.GetAwaiter().GetResult()</c>. Drive under <see cref="Evaluate{TSig}"/>, which
/// dispatches scoring on the thread pool.
/// </remarks>
public sealed class LLMAsJudgeMetric<TSig> : IMetric<TSig>, INamedMetric
    where TSig : ISignature
{
    /// <summary>Stable identifier <c>"llm_as_judge"</c>.</summary>
    public string Name => "llm_as_judge";

    readonly IChatClient _judge;
    readonly string _instructions;
    readonly LLMAsJudgeOptions _options;

    /// <summary>Legacy constructor. Delegates to the options-record overload with Samples=1.</summary>
    /// <param name="judgeClient">The <see cref="IChatClient"/> used to call the judge LM.</param>
    /// <param name="judgeInstructions">System-prompt instructions sent to the judge.</param>
    /// <param name="criteria">Optional list of scoring criteria; rendered as a numbered list in the user prompt.</param>
    /// <param name="traceSink">Optional sink to receive a <see cref="TraceRecord"/> after each judge call.</param>
    public LLMAsJudgeMetric(
        IChatClient judgeClient,
        string judgeInstructions,
        string[]? criteria = null,
        ITraceSink? traceSink = null)
        : this(judgeClient, judgeInstructions, new LLMAsJudgeOptions(1, JudgeAggregation.Median, criteria, traceSink))
    { }

    /// <summary>Initialises with explicit options for multi-sample judging.</summary>
    /// <param name="judgeClient">Judge chat client.</param>
    /// <param name="judgeInstructions">System-prompt instructions.</param>
    /// <param name="options">Options including Samples and Aggregation.</param>
    public LLMAsJudgeMetric(
        IChatClient judgeClient,
        string judgeInstructions,
        LLMAsJudgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(judgeClient);
        ArgumentNullException.ThrowIfNull(judgeInstructions);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Samples < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Samples must be >= 1.");
        _judge = judgeClient;
        _instructions = judgeInstructions;
        _options = options;
    }

    /// <inheritdoc/>
    public double Score(TSig actual, TSig expected, SignatureMetadata signature)
    {
        var messages = BuildPrompt(actual, expected, signature);
        if (_options.Samples == 1) return CallOnce(messages, signature);

        var tasks = new Task<double>[_options.Samples];
        for (var i = 0; i < _options.Samples; i++)
        {
            var msgsCapture = messages;
            tasks[i] = Task.Run(() => CallOnce(msgsCapture, signature));
        }
        Task.WhenAll(tasks).GetAwaiter().GetResult();
        var samples = new double[tasks.Length];
        for (var i = 0; i < tasks.Length; i++) samples[i] = tasks[i].Result;
        return Aggregate(samples, _options.Aggregation);
    }

    double CallOnce(List<ChatMessage> messages, SignatureMetadata signature)
    {
        using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.judge.score");
        activity?.SetTag("judge.signature", signature.SignatureType.Name);

        var sw = Stopwatch.StartNew();
        ChatResponse response;
        try
        {
            var ambient = AmpelumContext.Ambient;
            if (ambient is null)
            {
                response = _judge.GetResponseAsync(messages).GetAwaiter().GetResult();
            }
            else
            {
                var lmCtx = ambient with { Chat = _judge };
#pragma warning disable AMPELUM_V09_PREVIEW
                response = HookedLmCall.InvokeAsync(
                    lmCtx, "LLMAsJudgeMetric.Score", messages).GetAwaiter().GetResult();
#pragma warning restore AMPELUM_V09_PREVIEW
            }
        }
        catch (LmCallException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new LmCallException("LLMAsJudgeMetric judge call failed.", ex);
        }

        var text = response.Text!;
        double score;
        string reasoning;
        try
        {
            using var doc = JsonDocument.Parse(text);
            score = doc.RootElement.GetProperty("score").GetDouble();
            reasoning = doc.RootElement.TryGetProperty("reasoning", out var r) ? (r.GetString() ?? "") : "";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new AdapterParseException(signature.SignatureType.Name, nameof(LLMAsJudgeMetric<TSig>), text,
                "judge response was not parseable as {score: double, reasoning: string}");
        }

        sw.Stop();

        if (_options.TraceSink is not null)
        {
            var trace = new TraceRecord(
                Id: Guid.NewGuid(),
                ModuleName: "LLMAsJudgeMetric",
                SignatureType: typeof(TSig),
                Inputs: new Dictionary<string, object?>(),
                Outputs: new Dictionary<string, object?> { ["score"] = score },
                PromptMessages: messages,
                RawResponse: text,
                Usage: response.Usage,
                Duration: sw.Elapsed,
                Error: null,
                Metadata: new Dictionary<string, object?> { [TraceKeys.Reasoning] = reasoning });
            try { _options.TraceSink.Write(trace); } catch { /* never take down a metric */ }
        }

        return score;
    }

    static double Aggregate(double[] xs, JudgeAggregation a)
    {
        switch (a)
        {
            case JudgeAggregation.Mean:
                {
                    double s = 0; foreach (var x in xs) s += x; return s / xs.Length;
                }
            case JudgeAggregation.Median:
                {
                    var sorted = (double[])xs.Clone();
                    Array.Sort(sorted);
                    var n = sorted.Length;
                    return n % 2 == 1 ? sorted[n / 2] : 0.5 * (sorted[n / 2 - 1] + sorted[n / 2]);
                }
            case JudgeAggregation.MajorityVote:
                {
                    int z = 0, h = 0, o = 0;
                    foreach (var x in xs)
                    {
                        if (x < 0.25) z++;
                        else if (x < 0.75) h++;
                        else o++;
                    }
                    if (o >= h && o >= z) return 1.0;
                    if (h >= z) return 0.5;
                    return 0.0;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(a));
        }
    }

    List<ChatMessage> BuildPrompt(TSig actual, TSig expected, SignatureMetadata signature)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Inputs");
        foreach (var f in signature.Inputs) sb.Append(f.Name).Append(": ").AppendLine(Read(actual, f.Name));
        sb.AppendLine();
        sb.AppendLine("## Expected outputs");
        foreach (var f in signature.Outputs) sb.Append(f.Name).Append(": ").AppendLine(Read(expected, f.Name));
        sb.AppendLine();
        sb.AppendLine("## Actual outputs");
        foreach (var f in signature.Outputs) sb.Append(f.Name).Append(": ").AppendLine(Read(actual, f.Name));
        var criteria = _options.Criteria;
        if (criteria is { Length: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("## Criteria");
            for (var i = 0; i < criteria.Length; i++)
                sb.Append(i + 1).Append(". ").AppendLine(criteria[i]);
        }
        sb.AppendLine();
        sb.AppendLine("Respond with a JSON object: {\"score\": <float between 0.0 and 1.0>, \"reasoning\": \"<one sentence>\"}");
        return new List<ChatMessage>
        {
            new(ChatRole.System, _instructions),
            new(ChatRole.User, sb.ToString()),
        };
    }

    static string Read(object instance, string name)
    {
        var v = instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.GetValue(instance);
        return v switch
        {
            null => "",
            string s => s,
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => v.ToString()!,
        };
    }
}

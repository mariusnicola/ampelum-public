using System.Collections.Frozen;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace SignalGrid.Ampelum;

/// <summary>
/// Static entry-points for Ampelum's OpenTelemetry instrumentation.
/// Provides the <see cref="ActivitySource"/> for distributed tracing and the <see cref="System.Diagnostics.Metrics.Meter"/>
/// for metrics. Register both with your OTel SDK builder to collect telemetry.
/// </summary>
public static class Diagnostics
{
    static readonly string Version = typeof(Diagnostics).Assembly.GetName().Version!.ToString();

    /// <summary>OpenTelemetry <see cref="ActivitySource"/> for Ampelum spans. Source name: <c>"SignalGrid.Ampelum"</c>.</summary>
    public static readonly ActivitySource Source = new("SignalGrid.Ampelum", Version);

    /// <summary>OpenTelemetry <see cref="System.Diagnostics.Metrics.Meter"/> for Ampelum metrics. Meter name: <c>"SignalGrid.Ampelum"</c>.</summary>
    public static readonly Meter Meter = new("SignalGrid.Ampelum", Version);

    /// <summary>Histogram recording wall-clock duration of each <c>Module.ForwardAsync</c> call, in milliseconds.</summary>
    public static readonly Histogram<double> ModuleForwardDuration =
        Meter.CreateHistogram<double>("SignalGrid.Ampelum.module.forward.duration", "ms", "Duration of a Module.ForwardAsync call.");

    /// <summary>Counter of failed <c>ForwardAsync</c> invocations (any exception, including cancellation).</summary>
    public static readonly Counter<long> ModuleForwardErrors =
        Meter.CreateCounter<long>("SignalGrid.Ampelum.module.forward.errors", "errors", "Number of failed forward calls.");

    /// <summary>Histogram recording wall-clock duration of the underlying <c>IChatClient.GetResponseAsync</c> call, in milliseconds.</summary>
    public static readonly Histogram<double> LmCallDuration =
        Meter.CreateHistogram<double>("SignalGrid.Ampelum.lm.call.duration", "ms", "Duration of the IChatClient.GetResponseAsync call.");

    /// <summary>Histogram recording wall-clock duration of <see cref="IAdapter.Format"/>, in milliseconds.</summary>
    public static readonly Histogram<double> AdapterFormatDuration =
        Meter.CreateHistogram<double>("SignalGrid.Ampelum.adapter.format.duration", "ms",
            "Duration of IAdapter.Format on the input.");

    /// <summary>Histogram recording wall-clock duration of <see cref="IAdapter.Parse"/>, in milliseconds.</summary>
    public static readonly Histogram<double> AdapterParseDuration =
        Meter.CreateHistogram<double>("SignalGrid.Ampelum.adapter.parse.duration", "ms",
            "Duration of IAdapter.Parse on the response.");

    /// <summary>Histogram recording wall-clock duration of <c>HookPipeline.RunBeforeAsync</c>, in milliseconds.</summary>
    public static readonly Histogram<double> HookBeforeDuration =
        Meter.CreateHistogram<double>("SignalGrid.Ampelum.hook.before.duration", "ms",
            "Duration of IHook.OnBeforeAsync invocations across the hook pipeline.");

    /// <summary>Histogram recording wall-clock duration of <c>HookPipeline.RunAfterAsync</c>, in milliseconds.</summary>
    public static readonly Histogram<double> HookAfterDuration =
        Meter.CreateHistogram<double>("SignalGrid.Ampelum.hook.after.duration", "ms",
            "Duration of IHook.OnAfterAsync invocations across the hook pipeline.");

    /// <summary>Counter of trace records dropped due to <c>BatchedTraceSink</c> queue overflow.</summary>
    public static readonly Counter<long> TraceSinkDropped =
        Meter.CreateCounter<long>("SignalGrid.Ampelum.trace_sink.dropped", "{records}",
            "Trace records dropped due to BatchedTraceSink overflow.");

    /// <summary>Counter of input tokens reported by the LM provider.</summary>
    public static readonly Counter<long> TokensInput =
        Meter.CreateCounter<long>("SignalGrid.Ampelum.lm.tokens.input", "tokens", "Input tokens reported by provider.");

    /// <summary>Counter of output tokens reported by the LM provider.</summary>
    public static readonly Counter<long> TokensOutput =
        Meter.CreateCounter<long>("SignalGrid.Ampelum.lm.tokens.output", "tokens", "Output tokens reported by provider.");

    /// <summary>Counter of adapter parse failures (cases where the LM response could not be parsed into output fields).</summary>
    public static readonly Counter<long> AdapterParseFailures =
        Meter.CreateCounter<long>("SignalGrid.Ampelum.adapter.parse.failures", "failures", "Number of adapter parse failures.");

    /// <summary>Histogram recording wall-clock duration of each <c>StreamAsync</c> call, in milliseconds.</summary>
    public static readonly Histogram<double> ModuleStreamDuration =
        Meter.CreateHistogram<double>("SignalGrid.Ampelum.module.stream.duration", "ms", "Duration of a Module.StreamAsync call.");

    /// <summary>Histogram recording the number of streaming chunks received per <c>StreamAsync</c> call.</summary>
    public static readonly Histogram<long> ModuleStreamChunks =
        Meter.CreateHistogram<long>("SignalGrid.Ampelum.module.stream.chunks", "{chunks}", "Number of chunks received in a streaming call.");

    /// <summary>Histogram recording the number of iterations taken by a <see cref="ReAct{TSig}"/> forward pass.</summary>
    public static readonly Histogram<long> ReactIterations =
        Meter.CreateHistogram<long>("SignalGrid.Ampelum.react.iterations", "{iterations}", "Number of iterations in a ReAct forward pass.");

    /// <summary>Counter of individual tool invocations dispatched by <see cref="ReAct{TSig}"/>.</summary>
    public static readonly Counter<long> ReactToolCalls =
        Meter.CreateCounter<long>("SignalGrid.Ampelum.react.tool_calls", "{calls}", "Number of tool calls dispatched by ReAct.");

    /// <summary>Per-run mean score from <c>EvaluationResult.Mean</c>, unitless 0-1.</summary>
    public static readonly Histogram<double> EvaluateScore =
        Meter.CreateHistogram<double>("SignalGrid.Ampelum.evaluate.score", "1", "Per-run mean score from EvaluationResult.Mean.");

    /// <summary>Wall-clock duration of a complete <c>RunAsync</c> call, in milliseconds.</summary>
    public static readonly Histogram<double> EvaluateDuration =
        Meter.CreateHistogram<double>("SignalGrid.Ampelum.evaluate.duration", "ms", "Wall-clock duration of an Evaluate.RunAsync call.");

    /// <summary>Count of examples where <c>ExampleScore.Error</c> is non-null.</summary>
    public static readonly Counter<long> EvaluateFailures =
        Meter.CreateCounter<long>("SignalGrid.Ampelum.evaluate.failures", "{failures}", "Number of examples where ExampleScore.Error is non-null.");

    /// <summary>Total program invocations across all retry calls.</summary>
    public static readonly Counter<long> RetryAttempts =
        Meter.CreateCounter<long>("SignalGrid.Ampelum.retry.attempts", "{attempts}", "Total program invocations across all retry calls.");

    /// <summary>Which attempt index succeeded (0 = first try, 1 = first retry, etc.).</summary>
    public static readonly Histogram<long> RetrySucceededOnAttempt =
        Meter.CreateHistogram<long>("SignalGrid.Ampelum.retry.succeeded_on_attempt", "{attempt}", "Attempt index on which the program succeeded.");

    /// <summary>Wall-clock duration of a single <c>RetrieveAsync</c> call, in milliseconds.</summary>
    public static readonly Histogram<double> RetrieverQueryDuration =
        Meter.CreateHistogram<double>("SignalGrid.Ampelum.retriever.query.duration", "ms",
            "Wall-clock duration of a single RetrieveAsync call.");

    /// <summary>Number of documents returned per <c>RetrieveAsync</c> call.</summary>
    public static readonly Histogram<long> RetrieverQueryHits =
        Meter.CreateHistogram<long>("SignalGrid.Ampelum.retriever.query.hits", "{documents}",
            "Number of documents returned per RetrieveAsync call.");

    /// <summary>Wall-clock duration of a complete <c>IOptimizer.CompileAsync</c> call, in milliseconds.
    /// Note: spec Â§7.5 specifies seconds ("s") but "ms" is used here to match existing instrument
    /// conventions in this class (PSQ-2).</summary>
    public static readonly Histogram<double> OptimizerCompileDuration =
        Meter.CreateHistogram<double>("SignalGrid.Ampelum.optimizer.compile.duration", "ms",
            "Wall-clock duration of an IOptimizer.CompileAsync call.");

    /// <summary>Number of trainset examples used in a compile run.</summary>
    public static readonly Histogram<long> OptimizerCompileExamples =
        Meter.CreateHistogram<long>("SignalGrid.Ampelum.optimizer.compile.examples", "{examples}",
            "Number of trainset examples used in a compile run.");

    /// <summary>Mean score achieved on the trainset at the end of compilation, unitless 0-1.</summary>
    public static readonly Histogram<double> OptimizerCompileScore =
        Meter.CreateHistogram<double>("SignalGrid.Ampelum.optimizer.compile.score", "1",
            "Mean score on trainset at end of compilation.");

    // -------------------------------------------------------------------------------------------
    // High-cardinality attribute allowlist (v0.7 Task 9 / spec Â§8.3 G3 / PSQ-7).
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// When <c>true</c>, <see cref="SetTag"/> throws <see cref="InvalidOperationException"/>
    /// on attribute keys outside the v0.7 allowlist; when <c>false</c> (default), it logs a
    /// warning via <see cref="AttributePolicyLogger"/> if one is configured and emits the tag
    /// regardless. v0.7 keeps the guard warn-only; v0.9 promotes to throw under this flag.
    /// </summary>
    public static bool StrictAttributePolicy { get; set; }

    /// <summary>
    /// Optional logger that <see cref="SetTag"/> uses to emit a warning when an attribute key
    /// is not in the allowlist and <see cref="StrictAttributePolicy"/> is <c>false</c>. Set to
    /// <c>null</c> to disable warning emission entirely.
    /// </summary>
    public static ILogger? AttributePolicyLogger { get; set; }

    static readonly FrozenSet<string> AttributeAllowlist = new[]
    {
        // SignalGrid.Ampelum.* library internals (renamed in v1.0 rebrand).
        "SignalGrid.Ampelum.module", "SignalGrid.Ampelum.adapter", "SignalGrid.Ampelum.signature_type", "SignalGrid.Ampelum.hook_name",
        "SignalGrid.Ampelum.optimizer_type", "SignalGrid.Ampelum.trace_id", "SignalGrid.Ampelum.cache_hit",
        "SignalGrid.Ampelum.demos.count", "SignalGrid.Ampelum.retry.attempt", "SignalGrid.Ampelum.tool_call.name",
        "SignalGrid.Ampelum.tool.name", "SignalGrid.Ampelum.tool.success",
        // gen_ai.* OTel semconv 1.27.0 (Task 4).
        "gen_ai.system", "gen_ai.request.model", "gen_ai.response.model",
        "gen_ai.response.id", "gen_ai.usage.input_tokens",
        "gen_ai.usage.output_tokens", "gen_ai.operation.name",
        "gen_ai.semconv.version",
    }.ToFrozenSet();

    static bool IsAllowlistedPrefix(string key) =>
        key.StartsWith("exception.", StringComparison.Ordinal) ||
        key.StartsWith("otel.", StringComparison.Ordinal) ||
        key.StartsWith("module.", StringComparison.Ordinal) ||
        key.StartsWith("retriever.", StringComparison.Ordinal) ||
        key.StartsWith("query.", StringComparison.Ordinal) ||
        key.StartsWith("optimizer.", StringComparison.Ordinal) ||
        key.StartsWith("retry.", StringComparison.Ordinal) ||
        key.StartsWith("tool.", StringComparison.Ordinal) ||
        key.StartsWith("eval.", StringComparison.Ordinal) ||
        key.StartsWith("judge.", StringComparison.Ordinal) ||
        key.StartsWith("stream.", StringComparison.Ordinal) ||
        key.StartsWith("program.", StringComparison.Ordinal);

    /// <summary>
    /// Allowlist-checked wrapper around <see cref="Activity.SetTag"/>. v0.7 callers may opt into
    /// this wrapper to surface accidental high-cardinality / PII attribute keys before they
    /// escape into traces. Default behaviour (warn-only) does not change emission. v0.9 wires
    /// production callsites to this wrapper and promotes the StrictAttributePolicy flag.
    /// </summary>
    public static void SetTag(Activity? activity, string key, object? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (!AttributeAllowlist.Contains(key) && !IsAllowlistedPrefix(key))
        {
            if (StrictAttributePolicy)
            {
                throw new InvalidOperationException(
                    $"Attribute key '{key}' is not in the Ampelum allowlist; " +
                    "potential PII or high-cardinality leak. Set Diagnostics.StrictAttributePolicy=false to warn-only.");
            }
            AttributePolicyLogger?.LogWarning(
                "Attribute key '{Key}' is not in allowlist; potential PII / high-cardinality leak.",
                key);
        }
        activity?.SetTag(key, value);
    }
}

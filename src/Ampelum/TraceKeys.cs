namespace SignalGrid.Ampelum;

/// <summary>
/// Well-known keys for <see cref="TraceRecord.Metadata"/>. Keys are additive across versions;
/// a reader must tolerate missing keys. All values are documented in the spec's Â§2 schema.
/// </summary>
public static class TraceKeys
{
    /// <summary>Value type: <see langword="string"/>. Chain-of-thought reasoning text.
    /// Value lives in <c>Trace.Outputs[TraceKeys.Reasoning]</c>, NOT in Trace.Metadata.</summary>
    public const string Reasoning = "reasoning";

    /// <summary>Value type: <see cref="IReadOnlyList{T}"/> of <see cref="ToolCallRecord"/>.</summary>
    public const string ToolCalls = "tool_calls";

    /// <summary>Value type: <see langword="bool"/> (always <see langword="true"/> when present).
    /// Written to <see cref="TraceRecord.Metadata"/> when streaming adapter finalization
    /// throws <see cref="Exceptions.AdapterParseException"/>.</summary>
    public const string StreamingParseFailure = "streaming_parse_failure";

    /// <summary>Value type: <see langword="bool"/> (always <see langword="true"/> when present).
    /// Written to <see cref="TraceRecord.Metadata"/> when a response was served from the
    /// distributed cache rather than a live LM call.</summary>
    public const string CacheHit = "lm.cache_hit";

    /// <summary>Value type: <see langword="int"/>. 0-based index of the retry attempt that produced this trace,
    /// merged into <see cref="TraceRecord.Metadata"/> by <c>RetryModule&lt;TOut&gt;</c>.</summary>
    public const string RetryAttempt = "retry.attempt";

    /// <summary>Value type: <see cref="IReadOnlyList{T}"/> of <c>RetrievedDocument</c>.
    /// Convention: user RAG module code writes retrieved documents here after calling
    /// <c>context.Retriever!.RetrieveAsync</c> so they appear in <see cref="TraceRecord.Metadata"/>
    /// for observability and optimizer training. No library code writes this key automatically.</summary>
    public const string Citations = "citations";

    /// <summary>Value type: <see cref="IReadOnlyList{T}"/> of <c>Example</c>. Optimizer-bound modules
    /// write the demos injected at forward time here for observability and replay. v0.5a addition.</summary>
    public const string DemosUsed = "demos_used";

    /// <summary>Value type: <see langword="bool"/> (always <see langword="true"/> when present).
    /// Written to <see cref="TraceRecord.Metadata"/> when an <c>OnBefore</c> hook populated
    /// <c>HookContext.Outputs</c>, causing <c>Predict&lt;TSig&gt;.ForwardAsync</c> to skip the LM call.
    /// Distinguishes replay/cache short-circuit traces from live-LM traces. v0.6a addition (PSQ-11).</summary>
    public const string HookShortCircuited = "hook_short_circuited";
}

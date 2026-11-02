using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Composition;

/// <summary>
/// Well-known keys for <see cref="TraceRecord.Metadata"/> emitted by v0.7 composition
/// primitives. Once promoted to <c>PublicAPI.Shipped.txt</c>, the literal values are
/// frozen â€” observability dashboards key off these strings.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public static class TraceKeys
{
    // ---------- Shared (composition primitives in general) ----------

    /// <summary>Value type: <see cref="string"/>. The primitive that emitted this aggregate trace
    /// (<c>"Loop"</c>, <c>"Sequence"</c>, <c>"ConditionalBranch"</c>, ...).</summary>
    public const string CompositionPrimitive = "composition.primitive";

    /// <summary>Value type: <see cref="int"/>. Number of child traces aggregated. Always non-negative.</summary>
    public const string CompositionChildCount = "composition.child_count";

    /// <summary>Value type: <see cref="IReadOnlyList{T}"/> of <see cref="Guid"/>. Child trace IDs in execution order.</summary>
    public const string CompositionChildTraceIds = "composition.child_trace_ids";

    // ---------- Loop ----------

    /// <summary>Value type: <see cref="int"/>. Number of LM calls actually made by the loop (1-based).</summary>
    public const string LoopIterations = "loop.iterations";

    /// <summary>Value type: <see cref="bool"/>. <see langword="true"/> iff the until-predicate returned true.</summary>
    public const string LoopConverged = "loop.converged";

    /// <summary>Value type: <see cref="IReadOnlyList{T}"/> of <see cref="Guid"/>. Per-iteration child trace IDs.</summary>
    public const string LoopChildTraceIds = "loop.child_trace_ids";

    // ---------- ConditionalBranch ----------

    /// <summary>Value type: <see cref="string"/>. <c>"ifTrue"</c> when the if-true branch ran,
    /// <c>"ifFalse"</c> when the if-false branch ran. Absent when <c>route</c> threw before either branch ran.</summary>
    public const string BranchRoute = "branch.route";

    /// <summary>Value type: <see cref="IReadOnlyList{T}"/> of <see cref="Guid"/>. Child trace IDs in
    /// execution order (condition first, then the chosen branch).</summary>
    public const string BranchChildTraceIds = "branch.child_trace_ids";

    // ---------- Sequence ----------

    /// <summary>Value type: <see cref="int"/>. Number of signature-typed hops (2..5).</summary>
    public const string SequenceLength = "sequence.length";

    /// <summary>Value type: <see cref="IReadOnlyList{T}"/> of <see cref="Guid"/>. Child trace IDs in hop order.</summary>
    public const string SequenceChildTraceIds = "sequence.child_trace_ids";
}

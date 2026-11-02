using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Orchestration;

/// <summary>
/// Result returned by a loop predicate module. When <see cref="ShouldContinue"/> is <c>false</c>,
/// the loop terminates and <see cref="Reason"/> describes why.
/// </summary>
/// <typeparam name="TSig">Signature type (unused in the result itself, carried for type consistency).</typeparam>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record LoopPredicateResult<TSig>(bool ShouldContinue, string? Reason = null)
    where TSig : ISignature;
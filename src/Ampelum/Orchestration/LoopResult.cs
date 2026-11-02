using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Orchestration;

/// <summary>
/// Result of a <see cref="Loop{TSig}"/> execution. Contains the final state after loop termination,
/// the number of iterations executed, and the reason for stopping.
/// </summary>
/// <typeparam name="TSig">The signature type for the loop body.</typeparam>
/// <param name="FinalState">The final state value after the last body iteration.</param>
/// <param name="Iterations">Number of body iterations actually executed (â‰¥ 1).</param>
/// <param name="Reason">Human-readable reason for termination (converged, max iterations, error).</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record LoopResult<TSig>(TSig FinalState, int Iterations, string Reason)
    where TSig : ISignature;
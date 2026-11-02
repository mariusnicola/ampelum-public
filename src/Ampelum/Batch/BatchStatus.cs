using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Batch;

/// <summary>Lifecycle states of a provider batch (PSQ-4, v0.9g).</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public enum BatchState
{
    /// <summary>Batch is queued or running on the provider.</summary>
    InProgress = 0,

    /// <summary>Batch completed; results may include per-item failures.</summary>
    Completed = 1,

    /// <summary>Batch failed entirely (no per-item results available).</summary>
    Failed = 2,

    /// <summary>Batch was cancelled by the caller.</summary>
    Cancelled = 3,

    /// <summary>Batch expired (provider TTL exceeded before completion).</summary>
    Expired = 4,
}

/// <summary>
/// Snapshot of a batch's current progress (PSQ-4, v0.9g).
/// </summary>
/// <param name="State">Current lifecycle state.</param>
/// <param name="CompletedCount">Items with a final result (success or per-item failure).</param>
/// <param name="FailedCount">Items that errored.</param>
/// <param name="TotalCount">Total items submitted.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record BatchStatus(BatchState State, int CompletedCount, int FailedCount, int TotalCount);

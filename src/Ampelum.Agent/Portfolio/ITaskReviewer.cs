namespace SignalGrid.Ampelum.Agent;

/// <summary>Pluggable reviewer for a task's output. Each domain profile can register
/// its own reviewer. Multiple reviewers per task are supported.</summary>
public interface ITaskReviewer
{
    /// <summary>Domain this reviewer applies to (e.g., "software", "research", "content").</summary>
    string Domain { get; }

    /// <summary>Review the changes produced by a task.</summary>
    Task<ReviewResult> ReviewAsync(
        TaskDefinition task,
        IReadOnlyList<FileChange> changes,
        CancellationToken ct = default);
}

/// <summary>Result of a review pass.</summary>
public sealed record ReviewResult(
    string ReviewerId,
    bool Approved,
    IReadOnlyList<ReviewFinding> Findings
);

/// <summary>A single finding from a review pass.</summary>
public sealed record ReviewFinding(
    string Message,
    ReviewSeverity Severity,
    string? FilePath = null,
    int? Line = null
);

/// <summary>Severity of a review finding.</summary>
public enum ReviewSeverity { Info, Warning, Error }

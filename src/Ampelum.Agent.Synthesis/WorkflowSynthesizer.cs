using SignalGrid.Ampelum.Agent;

namespace SignalGrid.Ampelum.Agent.Synthesis;

/// <summary>Creates new Lobster workflow YAML files and improves existing ones
/// based on execution history. Generate → validate → retry loop via ISynthesisInfrastructure.
/// CreateAsync/ImproveAsync never write to the canonical/versioned path — they return a validated,
/// immutable candidate. Only PromoteAsync writes the candidate to the canonical path, making it live.</summary>
public interface IWorkflowSynthesizer
{
    /// <summary>Create a new workflow YAML candidate for the given task description and tool set.
    /// Does not write to <paramref name="outputPath"/> — call PromoteAsync to make it live.</summary>
    Task<WorkflowSynthesisResult> CreateAsync(
        string taskDescription,
        IReadOnlyList<ToolSurvey> availableTools,
        string outputPath,
        CancellationToken ct = default);

    /// <summary>Propose an improvement candidate for an existing workflow based on its execution history.
    /// Does not write to <paramref name="workflowPath"/> — call PromoteAsync to make it live.</summary>
    Task<WorkflowSynthesisResult> ImproveAsync(
        string workflowPath,
        ImprovementContext context,
        CancellationToken ct = default);

    /// <summary>Write a previously produced candidate to its canonical/versioned path, making it live.
    /// Re-validates <paramref name="candidate"/>.Content before writing. When the candidate's
    /// <see cref="WorkflowSynthesisCandidate.RequiresReview"/> is true, the caller must pass
    /// <paramref name="reviewApproved"/> = true to acknowledge the review gate — otherwise the
    /// promotion is refused and nothing is written.</summary>
    Task<WorkflowPromotionResult> PromoteAsync(
        WorkflowSynthesisCandidate candidate,
        bool reviewApproved = false,
        CancellationToken ct = default);
}

/// <summary>An immutable, validated workflow artifact that has not been written to the canonical path.
/// Produced by CreateAsync/ImproveAsync; consumed by PromoteAsync.</summary>
public sealed record WorkflowSynthesisCandidate(
    string Content,
    string TargetPath,
    string Version,
    bool RequiresReview   // true when metric change exceeds auto-approve threshold
);

/// <summary>Result of workflow synthesis or improvement. On success, Candidate carries the
/// validated artifact awaiting promotion; nothing has been written to the canonical path yet.</summary>
public sealed record WorkflowSynthesisResult(
    bool Success,
    WorkflowSynthesisCandidate? Candidate,
    IReadOnlyList<string> ValidationErrors
);

/// <summary>Result of promoting a candidate to its canonical/versioned path. On failure (validation
/// failed, or the candidate required review and was not approved), Errors carries the reason(s)
/// and nothing was written.</summary>
public sealed record WorkflowPromotionResult(
    bool Success,
    string? WorkflowPath,
    string? Version,
    IReadOnlyList<string> Errors
);

/// <summary>Context for workflow improvement — includes metrics and recent history.</summary>
public sealed record ImprovementContext(
    WorkflowMetrics CurrentMetrics,
    IReadOnlyList<PhaseContextEntry> RecentHistory,
    string ImprovementGoal    // e.g., "reduce avg duration", "increase success rate"
);

/// <summary>A single entry in the workflow's phase history.</summary>
public sealed record PhaseContextEntry(
    string PhaseName,
    bool Succeeded,
    double DurationSeconds,
    long TokensUsed,
    IReadOnlyDictionary<string, string> Metadata
);
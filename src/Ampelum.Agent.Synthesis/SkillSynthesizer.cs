namespace SignalGrid.Ampelum.Agent.Synthesis;

/// <summary>Creates new SKILL.md files and improves existing ones.
/// Uses IOptimizer of SkillProgram (MIPROv2/COPRO) for SystemPrompt improvement
/// — the optimizer is the right tool for instruction quality iteration.
/// The synthesizer owns everything else: scaffold, tool assignment, versioning.</summary>
public interface ISkillSynthesizer
{
    /// <summary>Create a new skill directory with SKILL.md from a task description.</summary>
    Task<SkillSynthesisResult> CreateAsync(
        string taskDescription,
        IReadOnlyList<SignalGrid.Ampelum.Agent.ToolSurvey> availableTools,
        string skillsDirectory,
        CancellationToken ct = default);

    /// <summary>Improve the SystemPrompt of an existing skill using execution history.
    /// Delegates prompt improvement to IOptimizer of SkillProgram.</summary>
    Task<SkillSynthesisResult> ImproveAsync(
        string skillName,
        string skillsDirectory,
        IReadOnlyList<SkillExecutionRecord> history,
        CancellationToken ct = default);
}

/// <summary>Result of skill synthesis or improvement.</summary>
public sealed record SkillSynthesisResult(
    bool Success,
    string? SkillName,
    string? Version,
    IReadOnlyList<string> ValidationErrors,
    bool RequiresReview
);

/// <summary>Record of a single skill execution — used as training data for improvement.</summary>
public sealed record SkillExecutionRecord(
    string RunInstanceId,
    string TaskDescription,
    bool Succeeded,
    string[] ToolsUsed,
    string? UserFeedback,    // optional — explicit positive/negative signal
    DateTimeOffset OccurredAt
);
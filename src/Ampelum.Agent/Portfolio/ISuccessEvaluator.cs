namespace SignalGrid.Ampelum.Agent;

/// <summary>Determines whether a task or workspace execution succeeded.
/// Domain profiles provide their own evaluator; the default assumes
/// "all tasks completed" (the current behavior).</summary>
public interface ISuccessEvaluator
{
    /// <summary>Evaluate a single task result. Returns a normalized score 0.0–1.0
    /// and a human-readable judgment.</summary>
    Task<SuccessJudgment> EvaluateAsync(
        TaskDefinition task,
        TaskExecutionResult result,
        CancellationToken ct = default);

    /// <summary>Evaluate an entire workspace execution.</summary>
    Task<SuccessJudgment> EvaluatePortfolioAsync(
        WorkspaceContext workspace,
        IReadOnlyList<TaskExecutionResult> results,
        CancellationToken ct = default);
}

/// <summary>Normalized judgment of a task or workspace execution.</summary>
public sealed record SuccessJudgment(
    double Score,       // 0.0 (failed) — 1.0 (perfect)
    bool Passed,        // Score >= threshold (default 0.8)
    string Summary,     // e.g., "5/7 sources cited; 2 missing citations"
    IReadOnlyList<string>? ActionItems = null
);

/// <summary>Default success evaluator: reproduces current behavior exactly.
/// All tasks completed with no errors → 1.0 / true. Any failure → 0.0 / false.</summary>
public sealed class DefaultSuccessEvaluator : ISuccessEvaluator
{
    public Task<SuccessJudgment> EvaluateAsync(
        TaskDefinition task,
        TaskExecutionResult result,
        CancellationToken ct = default)
    {
        var passed = result.Success;
        return Task.FromResult(new SuccessJudgment(
            Score: passed ? 1.0 : 0.0,
            Passed: passed,
            Summary: passed
                ? $"Task '{task.Id}' completed successfully"
                : $"Task '{task.Id}' failed: {result.Error ?? "unknown error"}",
            ActionItems: passed ? null : new[] { "Review task failure and retry" }
        ));
    }

    public Task<SuccessJudgment> EvaluatePortfolioAsync(
        WorkspaceContext workspace,
        IReadOnlyList<TaskExecutionResult> results,
        CancellationToken ct = default)
    {
        var allPassed = results.All(r => r.Success);
        var passedCount = results.Count(r => r.Success);
        return Task.FromResult(new SuccessJudgment(
            Score: results.Count == 0 ? 1.0 : (double)passedCount / results.Count,
            Passed: allPassed,
            Summary: results.Count == 0
                ? "No tasks executed"
                : $"{passedCount}/{results.Count} tasks completed successfully",
            ActionItems: allPassed ? null : results
                .Where(r => !r.Success)
                .Select(r => $"Review failed task: {r.Error ?? "unknown error"}")
                .ToList()
        ));
    }
}
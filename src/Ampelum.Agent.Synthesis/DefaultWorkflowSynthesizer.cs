using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Agent.Yaml;

namespace SignalGrid.Ampelum.Agent.Synthesis;

/// <summary>
/// Default implementation of IWorkflowSynthesizer.
/// CreateAsync: generates workflow YAML content using a simple inline validator, returns a candidate (no write).
/// ImproveAsync: loads current workflow, builds improvement prompt from execution history, bumps version if
/// needed, returns a candidate (no write).
/// PromoteAsync: writes a previously produced candidate to its canonical/versioned path via ISynthesisInfrastructure.
/// </summary>
public sealed class DefaultWorkflowSynthesizer : IWorkflowSynthesizer
{
    private readonly ISynthesisInfrastructure _infra;
    private const double StructuralReviewThreshold = 0.2; // require review if >20% structural change

    public DefaultWorkflowSynthesizer(ISynthesisInfrastructure infrastructure)
    {
        ArgumentNullException.ThrowIfNull(infrastructure);
        _infra = infrastructure;
    }

    public async Task<WorkflowSynthesisResult> CreateAsync(
        string taskDescription,
        IReadOnlyList<ToolSurvey> availableTools,
        string outputPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskDescription);
        ArgumentNullException.ThrowIfNull(availableTools);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var prompt = BuildCreatePrompt(taskDescription, availableTools);

        var result = await _infra.GenerateAndValidateAsync(
            prompt,
            existingContent: null,
            validator: ValidateWorkflowContent,
            ct: ct);

        if (!result.Valid)
            return new WorkflowSynthesisResult(false, null, result.ValidationErrors);

        var candidate = new WorkflowSynthesisCandidate(result.Content, outputPath, "1.0.0", false);

        return new WorkflowSynthesisResult(true, candidate, []);
    }

    public async Task<WorkflowSynthesisResult> ImproveAsync(
        string workflowPath,
        ImprovementContext context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowPath);
        ArgumentNullException.ThrowIfNull(context);

        if (!File.Exists(workflowPath))
            return new WorkflowSynthesisResult(false, null, [$"Workflow not found at {workflowPath}"]);

        var currentContent = await File.ReadAllTextAsync(workflowPath, ct);
        var currentValidationErrors = ValidateWorkflowContent(currentContent);
        if (currentValidationErrors.Length > 0)
            // Fail loud rather than let ExtractVersion silently fall back to "1.0.0" for content that no
            // longer compiles (e.g. schema drift) -- that fallback would otherwise dress up a version
            // regression as a normal minor bump on the eventual candidate.
            return new WorkflowSynthesisResult(false, null, [$"Existing workflow at '{workflowPath}' no longer conforms to the current schema:", .. currentValidationErrors]);
        var metrics = context.CurrentMetrics;

        var historyContext = context.RecentHistory.Count == 0
            ? "No execution history available."
            : string.Join("\n", context.RecentHistory.Select(h =>
                $"- Phase '{h.PhaseName}': {(h.Succeeded ? "succeeded" : "failed")} in {h.DurationSeconds:F1}s, {h.TokensUsed} tokens"));

        var prompt = $"""
            Improve the following workflow YAML based on execution history.

            Improvement goal: {context.ImprovementGoal}
            Success rate: {metrics.SuccessRate:P0}
            Average duration: {metrics.AvgDurationSeconds:F1}s
            P95 duration: {metrics.P95DurationSeconds:F1}s
            Total runs: {metrics.RunCount}

            Recent phase history:
            {historyContext}

            Current workflow:
            {currentContent}

            Provide an improved workflow YAML. Focus on: {context.ImprovementGoal}
            """;

        var result = await _infra.GenerateAndValidateAsync(
            prompt,
            existingContent: currentContent,
            validator: ValidateWorkflowContent,
            ct: ct);

        if (!result.Valid)
            return new WorkflowSynthesisResult(false, null, result.ValidationErrors);

        // Check if structural change (steps added/removed) — requires review
        var requiresReview = IsStructuralChange(currentContent, result.Content) ||
                             metrics.SuccessRate < 0.5; // Large degradation also requires review

        // Bump minor version
        var currentVersion = ExtractVersion(currentContent);
        var newVersion = BumpMinorVersion(currentVersion);

        var candidate = new WorkflowSynthesisCandidate(result.Content, workflowPath, newVersion, requiresReview);

        return new WorkflowSynthesisResult(true, candidate, []);
    }

    public async Task<WorkflowPromotionResult> PromoteAsync(
        WorkflowSynthesisCandidate candidate,
        bool reviewApproved = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var validationErrors = ValidateWorkflowContent(candidate.Content);
        if (validationErrors.Length > 0)
            return new WorkflowPromotionResult(false, null, null, validationErrors);

        if (candidate.RequiresReview && !reviewApproved)
            return new WorkflowPromotionResult(
                false,
                null,
                null,
                ["Candidate requires review approval before promotion; call PromoteAsync with reviewApproved: true after review."]);

        var writtenPath = await _infra.WriteVersionedAsync(candidate.TargetPath, candidate.Content, candidate.Version, ct);

        return new WorkflowPromotionResult(true, writtenPath, candidate.Version, []);
    }

    private static string BuildCreatePrompt(string taskDescription, IReadOnlyList<ToolSurvey> availableTools)
    {
        var toolList = availableTools.Count == 0
            ? "(none)"
            : string.Join(", ", availableTools.Select(t => t.Name));

        return $"""
            Create a workflow YAML file for the following task:
            Task: {taskDescription}
            Available tools: {toolList}

            The workflow YAML must conform to the Ampelum canonical workflow schema:
            - id: (stable workflow identifier, kebab-case)
            - version: (semver, start with 1.0.0)
            - steps: (list of steps; each has id, kind [sequential|branch|loop|tool|approval|wait], depends_on)

            Respond with only the workflow YAML content.
            """;
    }

    /// <summary>
    /// Validates via the official <see cref="WorkflowYamlCompiler"/> -- the same compiler any other workflow
    /// YAML in this system is validated against -- rather than a bespoke string-search check. A synthesized
    /// workflow that fails real structural validation (unknown fields, dangling dependencies, an unresolved
    /// tool allow-list, etc.) must not be treated as a valid candidate just because it contains the right
    /// substrings.
    /// </summary>
    private static string[] ValidateWorkflowContent(string content)
    {
        var result = WorkflowYamlCompiler.Compile(content);
        return result.Success ? [] : [.. result.Errors];
    }

    private static bool IsStructuralChange(string before, string after)
    {
        var beforeResult = WorkflowYamlCompiler.Compile(before);
        var afterResult = WorkflowYamlCompiler.Compile(after);
        // A candidate that doesn't compile is caught separately by ValidateWorkflowContent's own gate;
        // treat it here as structural (forces review) rather than silently comparing against zero steps.
        if (!beforeResult.Success || !afterResult.Success) return true;
        return beforeResult.Artifact!.Definition.Steps.Count != afterResult.Artifact!.Definition.Steps.Count;
    }

    private static string ExtractVersion(string content)
    {
        var result = WorkflowYamlCompiler.Compile(content);
        return result.Success ? result.Artifact!.Definition.Version : "1.0.0";
    }

    private static string BumpMinorVersion(string version)
    {
        var parts = version.Split('.');
        if (parts.Length < 3) return "1.1.0";
        if (!int.TryParse(parts[1], out var minor)) return "1.1.0";
        return $"{parts[0]}.{minor + 1}.0";
    }
}

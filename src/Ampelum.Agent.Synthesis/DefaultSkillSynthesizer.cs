using SignalGrid.Ampelum.Agent;

namespace SignalGrid.Ampelum.Agent.Synthesis;

/// <summary>
/// Default implementation of ISkillSynthesizer.
/// CreateAsync: generates SKILL.md content using a simple inline validator and writes via ISynthesisInfrastructure.
/// ImproveAsync: loads current manifest, builds improvement prompt from successful history, bumps version if needed.
/// </summary>
public sealed class DefaultSkillSynthesizer : ISkillSynthesizer
{
    private readonly ISynthesisInfrastructure _infra;
    private const double ReviewThreshold = 0.5; // require review if prompt changed by >50%

    public DefaultSkillSynthesizer(ISynthesisInfrastructure infrastructure)
    {
        ArgumentNullException.ThrowIfNull(infrastructure);
        _infra = infrastructure;
    }

    public async Task<SkillSynthesisResult> CreateAsync(
        string taskDescription,
        IReadOnlyList<ToolSurvey> availableTools,
        string skillsDirectory,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskDescription);
        ArgumentNullException.ThrowIfNull(availableTools);
        ArgumentException.ThrowIfNullOrWhiteSpace(skillsDirectory);

        var skillName = DeriveSkillName(taskDescription);
        var prompt = BuildCreatePrompt(taskDescription, availableTools);

        var result = await _infra.GenerateAndValidateAsync(
            prompt,
            existingContent: null,
            validator: ValidateSkillContent,
            ct: ct);

        if (!result.Valid)
            return new SkillSynthesisResult(false, skillName, null, result.ValidationErrors, false);

        var canonicalPath = Path.Combine(skillsDirectory, skillName, "SKILL.md");
        await _infra.WriteVersionedAsync(canonicalPath, result.Content, "1.0.0", ct);

        return new SkillSynthesisResult(true, skillName, "1.0.0", [], false);
    }

    public async Task<SkillSynthesisResult> ImproveAsync(
        string skillName,
        string skillsDirectory,
        IReadOnlyList<SkillExecutionRecord> history,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillName);
        ArgumentException.ThrowIfNullOrWhiteSpace(skillsDirectory);
        ArgumentNullException.ThrowIfNull(history);

        var canonicalPath = Path.Combine(skillsDirectory, skillName, "SKILL.md");

        if (!File.Exists(canonicalPath))
            return new SkillSynthesisResult(false, skillName, null, [$"Skill '{skillName}' not found at {canonicalPath}"], false);

        var currentContent = await File.ReadAllTextAsync(canonicalPath, ct);
        var successRate = history.Count == 0 ? 0.0
            : (double)history.Count(r => r.Succeeded) / history.Count;

        // Build improvement prompt from best history.
        var bestRecords = history
            .Where(r => r.Succeeded && r.UserFeedback != null)
            .OrderByDescending(r => r.OccurredAt)
            .Take(3)
            .ToList();

        var improvementHint = bestRecords.Count > 0
            ? string.Join("\n", bestRecords.Select(r => $"- Succeeded: {r.TaskDescription}"))
            : "No successful examples with feedback yet.";

        var prompt = $"""
            Improve the following SKILL.md system prompt based on execution history.
            Success rate: {successRate:P0}
            Recent successful executions:
            {improvementHint}

            Current content:
            {currentContent}

            Provide an improved SKILL.md with a better system prompt.
            """;

        var result = await _infra.GenerateAndValidateAsync(
            prompt,
            existingContent: currentContent,
            validator: ValidateSkillContent,
            ct: ct);

        if (!result.Valid)
            return new SkillSynthesisResult(false, skillName, null, result.ValidationErrors, false);

        // Determine if review required (large change).
        var requiresReview = CalculateSimilarity(currentContent, result.Content) < (1.0 - ReviewThreshold);

        // Bump minor version.
        var newVersion = BumpMinorVersion(ExtractVersion(currentContent));
        await _infra.WriteVersionedAsync(canonicalPath, result.Content, newVersion, ct);

        return new SkillSynthesisResult(true, skillName, newVersion, [], requiresReview);
    }

    private static string DeriveSkillName(string taskDescription)
    {
        // Take first 3-4 words, join with hyphens, lowercase.
        var words = taskDescription.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Take(3)
            .Select(w => w.ToLowerInvariant().Trim('.', ',', '!', '?'))
            .Where(w => w.Length > 0);
        return string.Join("-", words);
    }

    private static string BuildCreatePrompt(string taskDescription, IReadOnlyList<ToolSurvey> availableTools)
    {
        var toolList = availableTools.Count == 0
            ? "(none)"
            : string.Join(", ", availableTools.Select(t => t.Name));

        return $"""
            Create a SKILL.md file for the following task:
            Task: {taskDescription}
            Available tools: {toolList}

            The SKILL.md must have these required fields:
            - name: (skill name, kebab-case)
            - version: (semver, start with 1.0.0)
            - description: (brief description)
            - system_prompt: (detailed instructions for the LLM)

            Respond with only the SKILL.md content.
            """;
    }

    private static string[] ValidateSkillContent(string content)
    {
        var errors = new List<string>();
        if (!content.Contains("name:", StringComparison.OrdinalIgnoreCase)) errors.Add("Missing required field: name");
        if (!content.Contains("version:", StringComparison.OrdinalIgnoreCase)) errors.Add("Missing required field: version");
        if (!content.Contains("description:", StringComparison.OrdinalIgnoreCase)) errors.Add("Missing required field: description");
        if (!content.Contains("system_prompt:", StringComparison.OrdinalIgnoreCase)) errors.Add("Missing required field: system_prompt");
        return [.. errors];
    }

    private static double CalculateSimilarity(string a, string b)
    {
        // Simple overlap measure: shared words / total unique words.
        var wordsA = new HashSet<string>(a.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var wordsB = new HashSet<string>(b.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var intersection = wordsA.Intersect(wordsB).Count();
        var union = wordsA.Union(wordsB).Count();
        return union == 0 ? 1.0 : (double)intersection / union;
    }

    private static string ExtractVersion(string content)
    {
        var line = content.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("version:", StringComparison.OrdinalIgnoreCase));
        if (line == null) return "1.0.0";
        var parts = line.Split(':', 2);
        return parts.Length == 2 ? parts[1].Trim() : "1.0.0";
    }

    private static string BumpMinorVersion(string version)
    {
        var parts = version.Split('.');
        if (parts.Length < 3) return "1.1.0";
        if (!int.TryParse(parts[1], out var minor)) return "1.1.0";
        return $"{parts[0]}.{minor + 1}.0";
    }
}

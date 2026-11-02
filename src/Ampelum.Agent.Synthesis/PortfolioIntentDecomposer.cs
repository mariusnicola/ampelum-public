using System.Text.Json;

namespace SignalGrid.Ampelum.Agent.Synthesis;

/// <summary>LLM-backed implementation of IPortfolioIntentDecomposer.
/// Builds a prompt from the project summary, calls GenerateAndValidateAsync,
/// and parses the response JSON into TaskDefinition[].</summary>
public sealed class PortfolioIntentDecomposer : IPortfolioIntentDecomposer
{
    private readonly ISynthesisInfrastructure _infra;

    public PortfolioIntentDecomposer(ISynthesisInfrastructure infra)
    {
        _infra = infra;
    }

    public async Task<TaskDefinition[]> DecomposeAsync(
        string intent,
        ProjectSummary summary,
        WorkspaceContext workspace,
        CancellationToken ct = default)
    {
        // Sanitize before entering LLM context
        var safeIntent = OutboundSanitizer.Default
            .Sanitize(intent, ScanSource.UserInput).SanitizedContent;
        var safeSummaryText = BuildSummaryText(summary);

        var prompt = BuildPrompt(safeIntent, safeSummaryText, workspace.Label);

        var result = await _infra.GenerateAndValidateAsync(
            prompt,
            existingContent: null,
            validator: ValidateTasksJson,
            ct);

        if (!result.Valid || string.IsNullOrWhiteSpace(result.Content))
            return Array.Empty<TaskDefinition>();

        try
        {
            return JsonSerializer.Deserialize<TaskDefinition[]>(
                result.Content, JsonSerializerOptions.Web)
                ?? Array.Empty<TaskDefinition>();
        }
        catch (JsonException)
        {
            return Array.Empty<TaskDefinition>();
        }
    }

    private static string BuildSummaryText(ProjectSummary summary)
    {
        var rawText = string.Join(" | ",
            new[] { summary.CurrentPhase }
                .Concat(summary.TopBacklogItems)
                .Concat(summary.OpenBlockers)
                .Where(s => !string.IsNullOrWhiteSpace(s)));

        return OutboundSanitizer.Default.Sanitize(rawText, ScanSource.ToolResult).SanitizedContent;
    }

    private static string BuildPrompt(string intent, string summaryText, string workspaceLabel)
    {
        var jsonExample = """[{"id":"task-1","description":"...","taskType":"write-code","dependsOn":[]}]""";
        return $"""
            Workspace: {workspaceLabel}
            Project state: {summaryText}
            Intent: {intent}

            Decompose the intent into a task DAG for this specific workspace.
            Rules:
            - Each task must be self-contained and specific to this project's current state.
            - Use task types: write-code, update-docs, run-tests, audit, refactor, research, review.
            - Root tasks have empty dependsOn. Later tasks may depend on earlier ones by id.
            - Return 1–5 tasks maximum. Focus on the highest-value actions.

            Output ONLY a JSON array (no commentary, no markdown fences):
            {jsonExample}
            """;
    }

    private static string[] ValidateTasksJson(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return new[] { "Empty response" };

        // Strip markdown code fences if the LLM added them
        var clean = content.Trim();
        if (clean.StartsWith("```")) clean = StripFences(clean);

        try
        {
            var elements = JsonSerializer.Deserialize<JsonElement[]>(
                clean, JsonSerializerOptions.Web);
            if (elements is null || elements.Length == 0)
                return new[] { "Empty task array" };

            var errors = new List<string>();
            foreach (var el in elements)
            {
                if (!el.TryGetProperty("id", out _))
                    errors.Add("Task missing 'id'");
                if (!el.TryGetProperty("description", out _))
                    errors.Add("Task missing 'description'");
                if (!el.TryGetProperty("taskType", out _))
                    errors.Add("Task missing 'taskType'");
            }
            return errors.ToArray();
        }
        catch (JsonException ex)
        {
            return new[] { $"Invalid JSON: {ex.Message}" };
        }
    }

    private static string StripFences(string content)
    {
        var lines = content.Split('\n');
        var start = 0;
        var end = lines.Length - 1;
        if (lines[start].StartsWith("```")) start++;
        if (end > start && lines[end].TrimEnd() == "```") end--;
        return string.Join('\n', lines[start..(end + 1)]);
    }
}
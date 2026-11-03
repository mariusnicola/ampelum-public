using System.Text.Json;
using SignalGrid.Ampelum.Agent;

namespace Ampelum.Samples.AgentRuntime;

/// <summary>The two tools of the run, their manifests and the dispatcher that guards them.</summary>
public static class TriageTools
{
    /// <summary>Reads one issue and assesses it. Read-only, so it needs no confirmation.</summary>
    public const string Audit = "audit";

    /// <summary>Writes the triage report into the project. It needs a confirmation or a session grant.</summary>
    public const string PostReport = "post_report";

    /// <summary>Argument names of the two tools.</summary>
    public const string IssueArgument = "issue";
    public const string ContentArgument = "content";

    /// <summary>Manifests of both tools.</summary>
    public static IReadOnlyList<ToolManifest> Manifests { get; } =
    [
        new ToolManifest
        {
            Name = Audit,
            Description = "Assess one backlog issue against the code",
            DangerTier = DangerTier.Safe,
            Reversibility = Reversibility.Reversible,
        },
        new ToolManifest
        {
            Name = PostReport,
            Description = "Write the triage report into the project",
            DangerTier = DangerTier.Additive,
            Reversibility = Reversibility.DurableWithUndo,
            RequiresConfirmation = true,
        },
    ];

    /// <summary>Survey of the tools, as the planner sees them.</summary>
    public static IReadOnlyList<ToolSurvey> Surveys { get; } =
        [.. Manifests.Select(m => new ToolSurvey { Name = m.Name, Description = m.Description, DangerTier = m.DangerTier, RequiresConfirmation = m.RequiresConfirmation })];

    /// <summary>
    /// Creates the dispatcher. The policy engine consults <paramref name="grants"/>, so <see cref="PostReport"/> only runs
    /// once a person has approved the run and a grant for this caller exists.
    /// </summary>
    public static ToolDispatcher CreateDispatcher(
        AgentIdentity caller,
        SessionGrantStore grants,
        IReadOnlyList<TriageIssue> backlog,
        Func<TriageIssue, CancellationToken, Task<IssueResult>> audit,
        ReportWriter writer,
        SessionBudgetMeter? budget = null)
    {
        var dispatcher = new ToolDispatcher(new ManifestToolLoader(Manifests), new InMemoryTokenStore(), new PolicyEngine([], grants), caller, budget: budget);
        dispatcher.RegisterHandler(Audit, async (args, ct) =>
        {
            var number = args[IssueArgument].GetInt32();
            var issue = backlog.Single(i => i.Number == number);
            try
            {
                var result = await audit(issue, ct).ConfigureAwait(false);
                return new ToolResult(true, JsonSerializer.Serialize(result), null, null);
            }
            catch (Exception ex) when (BudgetExhaustedException.Find(ex) is { } budget)
            {
                return new ToolResult(false, null, $"issue {number} deferred: {budget.Message}", null);
            }
        });
        dispatcher.RegisterHandler(PostReport, async (args, ct) =>
        {
            var path = await writer.WriteAsync(args[ContentArgument].GetString()!, ct).ConfigureAwait(false);
            return new ToolResult(true, path, null, null);
        });
        return dispatcher;
    }
}

/// <summary>Writes the report file, and only inside the workspace's blast radius.</summary>
public sealed class ReportWriter(string root, BlastRadiusScope scope)
{
    /// <summary>File name of the report in the project root.</summary>
    public const string FileName = "triage-report.md";

    public async Task<string> WriteAsync(string content, CancellationToken ct)
    {
        var path = Path.Combine(root, FileName);
        if (!scope.ContainsPath(path))
            throw new InvalidOperationException($"'{path}' is outside the workspace blast radius.");
        await File.WriteAllTextAsync(path, content, ct).ConfigureAwait(false);
        return path;
    }
}

/// <summary>Fixed catalogue of tools handed to the dispatcher.</summary>
internal sealed class ManifestToolLoader(IEnumerable<ToolManifest> manifests) : IToolLoader
{
    readonly Dictionary<string, ToolManifest> _manifests = manifests.ToDictionary(m => m.Name, StringComparer.Ordinal);

    public Task<IReadOnlyList<ToolSurvey>> SurveyAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ToolSurvey>>([.. _manifests.Values
            .Select(m => new ToolSurvey { Name = m.Name, Description = m.Description, DangerTier = m.DangerTier, RequiresConfirmation = m.RequiresConfirmation })]);

    public Task<ToolManifest> UnpackAsync(string toolName, CancellationToken ct = default) =>
        Task.FromResult(_manifests.TryGetValue(toolName, out var manifest)
            ? manifest
            : throw new KeyNotFoundException($"Tool '{toolName}' not found."));

    public Task<ToolManifest?> TryUnpackAsync(string toolName, CancellationToken ct = default) =>
        Task.FromResult(_manifests.GetValueOrDefault(toolName));

    public ValueTask EvictAsync(string toolName) => ValueTask.CompletedTask;

    public async Task<IReadOnlyList<ToolSurvey>> SearchSurveyAsync(string query, CancellationToken ct = default) =>
        [.. (await SurveyAsync(ct).ConfigureAwait(false))
            .Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || s.Description.Contains(query, StringComparison.OrdinalIgnoreCase))];
}

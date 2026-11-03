using System.Text.Json;
using SignalGrid.Ampelum.Agent;

namespace Ampelum.Samples.AgentRuntime;

/// <summary>
/// Limits and routing the project sets for its own triage runs, read from triage-policy.json in the project root.
/// Keeping them in the project means the sample code carries no limit of its own.
/// </summary>
public sealed record TriagePolicy(
    int MaxModelCalls,
    long MaxTokens,
    IReadOnlyList<string> ReadablePaths,
    int GrantMinutes,
    IReadOnlyDictionary<string, string> Routing)
{
    const string FileName = "triage-policy.json";
    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Reads the policy file from a project root.</summary>
    public static TriagePolicy Load(string root) =>
        JsonSerializer.Deserialize<TriagePolicy>(File.ReadAllText(Path.Combine(root, FileName)), Options)
            ?? throw new InvalidOperationException($"{FileName} is empty.");

    /// <summary>The session budget these limits describe.</summary>
    public SessionBudget ToBudget() => new() { MaxCallCount = MaxModelCalls, MaxTokens = MaxTokens };

    /// <summary>Routing rules in table order, from the policy's task type to tier map.</summary>
    public IReadOnlyList<RoutingRule> ToRules() =>
        [.. Routing.Select(pair => new RoutingRule
        {
            TaskType = pair.Key,
            Tier = Enum.Parse<ModelTier>(pair.Value, ignoreCase: true),
            Reason = $"Set by {FileName}",
        })];
}

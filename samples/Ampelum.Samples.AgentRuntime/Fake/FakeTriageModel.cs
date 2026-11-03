using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Ampelum.Samples.AgentRuntime;

/// <summary>One call the fake model received.</summary>
public sealed record ModelCall(string Prompt, string? ModelId);

/// <summary>
/// Stand-in for the language model when no key is configured. It answers from the sample's own fake data files
/// and chooses the answer by what the prompt asks for. It proves the wiring around the model, not any model quality.
/// </summary>
public sealed partial class FakeTriageModel : IChatClient
{
    const int TokensPerCall = 100;

    readonly object _gate = new();
    readonly List<ModelCall> _calls = [];
    readonly Func<int, string> _refineVerdict;
    readonly Dictionary<string, JsonElement> _assessments;
    readonly Dictionary<string, string> _skillPicks;
    int _refineCalls;

    /// <summary>Model ids sent with the three kinds of call, for tests and demo output.</summary>
    public static ModelTiers Tiers { get; } = new("fake-fast-model", "fake-balanced-model", "fake-capable-model", "fake");

    /// <summary>Creates the fake. <paramref name="refineVerdict"/> maps the refine call number (from 1) to revise or ok.</summary>
    public FakeTriageModel(Func<int, string>? refineVerdict = null)
    {
        _refineVerdict = refineVerdict ?? (n => n == 1 ? "revise" : "ok");
        _assessments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Embedded.ReadText("fake/assessments.json"))!;
        _skillPicks = JsonSerializer.Deserialize<Dictionary<string, string>>(Embedded.ReadText("fake/skill-picks.json"))!;
    }

    /// <summary>Every call so far, in arrival order.</summary>
    public IReadOnlyList<ModelCall> Calls
    {
        get { lock (_gate) return [.. _calls]; }
    }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var prompt = string.Join("\n", messages.Select(m => m.Text));
        lock (_gate)
            _calls.Add(new ModelCall(prompt, options?.ModelId));

        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, Answer(prompt)))
        {
            Usage = new UsageDetails { InputTokenCount = TokensPerCall / 2, OutputTokenCount = TokensPerCall / 2, TotalTokenCount = TokensPerCall },
        };
        return Task.FromResult(response);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The triage run does not stream.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    string Answer(string prompt)
    {
        if (prompt.Contains("Decompose the intent", StringComparison.Ordinal))
            return Embedded.ReadText("fake/tasks.json");
        if (prompt.Contains("Create a workflow YAML", StringComparison.Ordinal))
            return Embedded.ReadText("fake/plan.yml");
        if (prompt.Contains("Select the best skill(s) for", StringComparison.Ordinal))
            return SkillPick(prompt);
        if (prompt.Contains("Refine the report draft", StringComparison.Ordinal))
            return Refinement();
        if (prompt.Contains("Assess the issue from the brief", StringComparison.Ordinal))
            return Assessment(prompt);
        throw new InvalidOperationException("FakeTriageModel has no answer for this prompt.");
    }

    string SkillPick(string prompt)
    {
        var issue = IssueNumber(prompt);
        var picked = _skillPicks.TryGetValue(issue, out var name) ? $"\"{name}\"" : "";
        return $$"""{"selected": [{{picked}}], "reasoning": "scripted"}""";
    }

    string Assessment(string prompt)
    {
        var answer = _assessments[IssueNumber(prompt)];
        return Fields(
            ("Severity", answer.GetProperty("severity").GetString()!),
            ("Cause", answer.GetProperty("cause").GetString()!),
            ("NextStep", answer.GetProperty("nextStep").GetString()!));
    }

    string Refinement()
    {
        int call;
        lock (_gate)
            call = ++_refineCalls;
        return Fields(("Verdict", _refineVerdict(call)), ("Note", $"Editor pass {call}."));
    }

    static string Fields(params (string Name, string Value)[] fields) =>
        string.Join("\n\n", fields.Select(f => $"[[ ## {f.Name} ## ]]\n{f.Value}"));

    static string IssueNumber(string prompt) => IssueMarker().Match(prompt).Groups["n"].Value;

    [GeneratedRegex(@"#(?<n>\d+)")]
    private static partial Regex IssueMarker();
}

using System.Text.Json;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Hooks;

namespace SignalGrid.Ampelum.Agent;

/// <summary>
/// LLM-powered <see cref="ISkillSelector"/> that uses a chat model to pick the best
/// skill when tag matching is ambiguous or empty. Falls back to <see cref="TagSkillSelector"/>
/// when no <see cref="IChatClient"/> is provided, or when LLM response parsing fails.
/// </summary>
public sealed class LlmSkillSelector : ISkillSelector
{
    /// <summary>
    /// Prompt template for LLM-based skill selection.
    /// Placeholders: {task}, {skills_json}
    /// </summary>
    public const string PromptTemplate =
        "Select the best skill(s) for: {task}. Available skills: {skills_json}. Return JSON: {{\"selected\": [\"skill-name\", ...], \"reasoning\": \"...\"}}";

    private readonly IChatClient? _llm;
    private readonly ISkillSelector _fallback;
    private readonly string _taskDescription;

    /// <summary>
    /// Create an LLM-powered skill selector.
    /// </summary>
    /// <param name="llm">Optional chat client. When null, delegates to <paramref name="fallback"/>.</param>
    /// <param name="fallback">Fallback selector used when LLM is unavailable or response is invalid.
    /// Defaults to <see cref="TagSkillSelector"/>.</param>
    /// <param name="taskDescription">Description of the task passed in the LLM prompt.
    /// Defaults to "Select skills for the current task".</param>
    public LlmSkillSelector(
        IChatClient? llm = null,
        ISkillSelector? fallback = null,
        string? taskDescription = null)
    {
        _llm = llm;
        _fallback = fallback ?? new TagSkillSelector();
        _taskDescription = taskDescription ?? "Select skills for the current task";
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SkillManifest>> SelectAsync(
        IReadOnlyList<SkillManifest> skills,
        IReadOnlyList<string> tags,
        CancellationToken ct = default)
    {
        if (_llm is null)
            return await _fallback.SelectAsync(skills, tags, ct);

        // Build prompt and call LLM
        var skillsJson = JsonSerializer.Serialize(skills.Select(s => new { s.Name, s.Description, s.Tags }));
        var prompt = PromptTemplate
            .Replace("{task}", _taskDescription)
            .Replace("{skills_json}", skillsJson);

        try
        {
            var messages = new[] { new ChatMessage(ChatRole.User, prompt) };
            var ambient = AmpelumContext.Ambient;
            var response = ambient is null
                ? await _llm.GetResponseAsync(messages, cancellationToken: ct)
                : await HookedLmCall.InvokeAsync(ambient with { Chat = _llm }, "LlmSkillSelector.Select", messages, cancellationToken: ct);

            var text = response.Text;

            if (string.IsNullOrWhiteSpace(text))
                return [];

            // Parse JSON response
            using var doc = JsonDocument.Parse(text);
            var selected = doc.RootElement.GetProperty("selected");

            if (selected.ValueKind != JsonValueKind.Array)
                return [];

            var selectedNames = new HashSet<string>(selected.EnumerateArray().Select(e => e.GetString() ?? string.Empty));
            selectedNames.Remove(string.Empty);

            if (selectedNames.Count == 0)
                return [];

            // Match against available skills
            var matched = skills.Where(s => selectedNames.Contains(s.Name)).ToArray();

            if (matched.Length > 0)
                return matched;

            // LLM returned names that don't match any skill → fallback to tag matching
            return await _fallback.SelectAsync(skills, tags, ct);
        }
        catch (JsonException)
        {
            // Malformed JSON → return empty
            return [];
        }
    }
}

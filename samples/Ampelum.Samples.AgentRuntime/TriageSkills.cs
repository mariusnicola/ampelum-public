using System.Text.Json;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Agent;

namespace Ampelum.Samples.AgentRuntime;

/// <summary>
/// Picks the skill whose prompt guides the assessment of one issue. The choice gets looser step by step:
/// skills carrying every label, then the model's pick, then skills sharing any label.
/// </summary>
public sealed class TriageSkills
{
    readonly SkillRegistry _registry = new();

    /// <summary>The registry, so a workspace can carry the same skills.</summary>
    public ISkillRegistry Registry => _registry;

    /// <summary>Registers the skills listed in skills.json. SkillRegistry.LoadAsync is a placeholder, so the file is read here.</summary>
    public TriageSkills()
    {
        var skills = JsonSerializer.Deserialize<SkillManifest[]>(Embedded.ReadText("skills.json"), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("skills.json is empty.");
        foreach (var skill in skills)
            _registry.Register(skill);
    }

    /// <summary>How the skill was found, for the report.</summary>
    public enum Route { ExactTags, Model, AnyTag, None }

    /// <summary>The chosen skill and the route that found it.</summary>
    public sealed record Choice(SkillManifest? Skill, Route Route);

    /// <summary>Chooses a skill for an issue. <paramref name="classifier"/> is only called when no skill carries all the labels.</summary>
    public async Task<Choice> ChooseAsync(TriageIssue issue, IChatClient classifier, CancellationToken ct = default)
    {
        var active = _registry.GetAll().Where(s => !s.Deprecated).ToArray();

        var exact = await new TagSkillSelector().SelectAsync(active, issue.Labels, ct).ConfigureAwait(false);
        if (exact.Count > 0)
            return new Choice(exact[0], Route.ExactTags);

        var asked = await new LlmSkillSelector(classifier, taskDescription: $"Triage issue {issue.Headline}")
            .SelectAsync(active, issue.Labels, ct).ConfigureAwait(false);
        if (asked.Count > 0)
            return new Choice(asked[0], Route.Model);

        var loose = _registry.SelectByTags(issue.Labels);
        return loose.Count > 0 ? new Choice(loose[0], Route.AnyTag) : new Choice(null, Route.None);
    }
}

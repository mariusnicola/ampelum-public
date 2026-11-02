using System.Collections.Concurrent;

namespace SignalGrid.Ampelum.Agent;

/// <summary>Thread-safe registry of known skills. Matches tasks to skills
/// by exact name or tag intersection.</summary>
public sealed class SkillRegistry : ISkillRegistry
{
    private readonly ConcurrentDictionary<string, SkillManifest> _skills = new();

    /// <inheritdoc />
    public Task LoadAsync(string skillsDirectory, CancellationToken ct = default)
    {
        // Placeholder — dynamic loading from YAML/JSON files
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IReadOnlyList<SkillManifest> GetAll() =>
        _skills.Values.OrderBy(s => s.Name).ToArray();

    /// <inheritdoc />
    public SkillManifest? GetByName(string name)
    {
        var key = _skills.Keys.FirstOrDefault(k =>
            k.Equals(name, StringComparison.OrdinalIgnoreCase));
        return key is not null && _skills.TryGetValue(key, out var s) ? s : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<SkillManifest> SelectByTags(IReadOnlyList<string> tags)
    {
        if (tags.Count == 0)
            return [];

        return _skills.Values
            .Where(s => !s.Deprecated)
            .Where(s => s.Tags.Any(t => tags.Any(rt =>
                t.Equals(rt, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(s => s.Tags.Count(t => tags.Any(rt =>
                t.Equals(rt, StringComparison.OrdinalIgnoreCase))))
            .ToArray();
    }

    /// <inheritdoc />
    public Task<SkillManifest?> SelectSemanticAsync(
        string taskDescription,
        CancellationToken ct = default)
    {
        // Placeholder — LLM-driven selection
        return Task.FromResult<SkillManifest?>(null);
    }

    /// <summary>Register a skill. Thread-safe.</summary>
    public void Register(SkillManifest skill)
    {
        _skills[skill.Name] = skill;
    }

    /// <summary>Match a task node to skills by exact required skill
    /// or tag intersection with task type.</summary>
    public IReadOnlyList<SkillManifest> Match(TaskNode task)
    {
        // Exact match by task type as skill name
        if (!string.IsNullOrWhiteSpace(task.TaskType) &&
            _skills.TryGetValue(task.TaskType, out var exact))
            return [exact];

        // Fallback: match by tag intersection with task type
        return _skills.Values
            .Where(s => !s.Deprecated)
            .Where(s => s.Tags.Any(t =>
                t.Equals(task.TaskType, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(s => s.Name)
            .ToArray();
    }

    /// <summary>Deprecate a skill with optional migration note.</summary>
    public void Deprecate(string name, string? migrationNote = null)
    {
        if (_skills.TryGetValue(name, out var skill))
            _skills[name] = skill with { Deprecated = true };
    }
}
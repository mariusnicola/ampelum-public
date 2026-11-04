using System.Collections.Concurrent;
using System.Text.Json;

namespace SignalGrid.Ampelum.Agent;

/// <summary>Thread-safe registry of known skills. Matches tasks to skills
/// by exact name or tag intersection.</summary>
public sealed class SkillRegistry : ISkillRegistry
{
    private readonly ConcurrentDictionary<string, SkillManifest> _skills = new();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Registers the skills of every <c>*.json</c> file directly in <paramref name="skillsDirectory"/>, in file
    /// name order. A file holds one <see cref="SkillManifest"/> object or an array of them; a later skill of the same name
    /// replaces an earlier one. All files are read before any skill is registered, so a file that is not valid leaves
    /// the registry as it was. Throws <see cref="DirectoryNotFoundException"/> when the directory does not exist.</summary>
    public async Task LoadAsync(string skillsDirectory, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(skillsDirectory);
        if (!Directory.Exists(skillsDirectory))
            throw new DirectoryNotFoundException($"Skills directory '{skillsDirectory}' does not exist.");

        var loaded = new List<SkillManifest>();
        foreach (var file in Directory.GetFiles(skillsDirectory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            var json = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var found = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.Deserialize<SkillManifest?[]>(JsonOptions) ?? []
                : [doc.RootElement.Deserialize<SkillManifest>(JsonOptions)];
            foreach (var skill in found)
            {
                // The required members only reject a missing property, so null and empty values are checked here.
                if (skill is null || string.IsNullOrWhiteSpace(skill.Name) || string.IsNullOrWhiteSpace(skill.Version))
                    throw new JsonException($"'{file}' holds a skill without a name or a version.");
                loaded.Add(skill);
            }
        }

        foreach (var skill in loaded)
            Register(skill);
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
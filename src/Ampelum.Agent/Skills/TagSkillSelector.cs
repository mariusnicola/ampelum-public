namespace SignalGrid.Ampelum.Agent;

/// <summary>Filters skills by tag intersection (AND logic).
/// Only skills that have ALL requested tags are returned. When no tags
/// are requested, all skills are returned as-is.</summary>
public sealed class TagSkillSelector : ISkillSelector
{
    private readonly StringComparison _comparison;

    /// <summary>Create a tag-based skill selector.</summary>
    /// <param name="comparison">String comparison for tag matching.
    /// Defaults to <see cref="StringComparison.OrdinalIgnoreCase"/>.</param>
    public TagSkillSelector(StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        _comparison = comparison;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SkillManifest>> SelectAsync(
        IReadOnlyList<SkillManifest> skills,
        IReadOnlyList<string> tags,
        CancellationToken ct = default)
    {
        if (tags.Count == 0)
            return Task.FromResult(skills);

        var result = skills
            .Where(s => tags.All(t => s.Tags.Any(st => st.Equals(t, _comparison))))
            .ToArray();

        return Task.FromResult<IReadOnlyList<SkillManifest>>(result);
    }
}

namespace SignalGrid.Ampelum.Agent;

/// <summary>Parsed SKILL.md manifest. Each skill defines what tools it owns and requires,
/// a default system prompt, and metadata for selection.</summary>
public sealed record SkillManifest
{
    /// <summary>Unique name. Must be a valid directory name (no `/`, `\`, `:`).</summary>
    public required string Name { get; init; }

    /// <summary>Semantic version.</summary>
    public required string Version { get; init; }

    /// <summary>Human-readable description. Shown to LLM during skill selection.</summary>
    public string Description { get; init; } = "";

    /// <summary>Tags for tag-based selection (e.g., "code", "backend", "python").</summary>
    public string[] Tags { get; init; } = [];

    /// <summary>Global tool names this skill requires (loaded alongside skill).</summary>
    public string[] Requires { get; init; } = [];

    /// <summary>Tool names this skill owns (defined in its tools/ subdirectory).</summary>
    public string[] Tools { get; init; } = [];

    /// <summary>True if this skill is always loaded alongside any active skill.</summary>
    public bool IsDefault { get; init; }

    /// <summary>True if this skill is deprecated. Not auto-selected but findable by name.</summary>
    public bool Deprecated { get; init; }

    /// <summary>The system prompt for this skill — the core instruction text.</summary>
    public string SystemPrompt { get; init; } = "";
}

/// <summary>Registry that loads and manages skills. Supports hot-reload via FileSystemWatcher.</summary>
public interface ISkillRegistry
{
    /// <summary>Load all skills from a directory. Called once at startup.</summary>
    Task LoadAsync(string skillsDirectory, CancellationToken ct = default);

    /// <summary>List all loaded skills.</summary>
    IReadOnlyList<SkillManifest> GetAll();

    /// <summary>Find a skill by exact name (case-insensitive). Returns null if not found.</summary>
    SkillManifest? GetByName(string name);

    /// <summary>Select a skill by tags. Returns skills sorted by tag overlap (best match first).</summary>
    IReadOnlyList<SkillManifest> SelectByTags(IReadOnlyList<string> tags);

    /// <summary>Select a skill by task description (LLM-driven survey-only selection).</summary>
    Task<SkillManifest?> SelectSemanticAsync(string taskDescription, CancellationToken ct = default);
}

/// <summary>Skill selection strategy for filtering skills by tag intersection.</summary>
public interface ISkillSelector
{
    /// <summary>Filter skills by tag intersection. When <paramref name="tags"/> is empty,
    /// all skills are returned. Otherwise only skills that have ALL requested tags are returned.</summary>
    Task<IReadOnlyList<SkillManifest>> SelectAsync(
        IReadOnlyList<SkillManifest> skills,
        IReadOnlyList<string> tags,
        CancellationToken ct = default);
}
namespace SignalGrid.Ampelum.Agent;

/// <summary>Loads tool manifests and provides progressive discovery.
/// SurveyAsync returns minimal summaries (name + description + dangerTier) to conserve tokens.
/// UnpackAsync returns the full manifest only when a tool is actually being invoked.
/// The LLM never calls Unpack — only ToolDispatcher does.</summary>
public interface IToolLoader
{
    /// <summary>Survey: return all known tools as lightweight summaries.
    /// Max ~30 results to fit within token budget.</summary>
    Task<IReadOnlyList<ToolSurvey>> SurveyAsync(CancellationToken ct = default);

    /// <summary>Unpack: return full manifest for a specific tool.
    /// Called by ToolDispatcher, not the LLM. Cached after first load.
    /// Throws KeyNotFoundException if tool not found.</summary>
    Task<ToolManifest> UnpackAsync(string toolName, CancellationToken ct = default);

    /// <summary>Unpack: return full manifest for a specific tool, or null if not found.
    /// Called by ToolDispatcher, not the LLM. Cached after first load.</summary>
    Task<ToolManifest?> TryUnpackAsync(string toolName, CancellationToken ct = default);

    /// <summary>Evict a tool from the cache. Next access will re-fetch.
    /// Used for hot-reload and explicit invalidation.</summary>
    ValueTask EvictAsync(string toolName);

    /// <summary>Search for tools by name or description.
    /// Returns up to 10 results. Used when active skill has >30 tools.</summary>
    Task<IReadOnlyList<ToolSurvey>> SearchSurveyAsync(string query, CancellationToken ct = default);
}

/// <summary>Plugin-based tool source — custom tools loaded from external assemblies or MCP servers.
/// Each IToolSource contributes its tools to the global IToolLoader.</summary>
public interface IToolSource
{
    /// <summary>Name prefix for this source's tools — prevents naming collisions.
    /// E.g., "mcp/github" or "ampelum/builtin".</summary>
    string SourcePrefix { get; }

    /// <summary>Survey this source's tools.</summary>
    Task<IReadOnlyList<ToolSurvey>> SurveyAsync(CancellationToken ct = default);

    /// <summary>Unpack a tool from this source.</summary>
    Task<ToolManifest?> TryUnpackAsync(string toolName, CancellationToken ct = default);
}

/// <summary>File-based tool source — tools loaded from YAML files in a directory.
/// Supports hot-reload via FileSystemWatcher.</summary>
public interface IFileToolSource : IToolSource
{
    /// <summary>Root directory where tool YAML files are stored.</summary>
    string SourceDirectory { get; }
}
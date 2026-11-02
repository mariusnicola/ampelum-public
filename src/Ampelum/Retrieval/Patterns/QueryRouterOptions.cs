using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Retrieval.Patterns;

/// <summary>Options for <see cref="QueryRouter"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class QueryRouterOptions
{
    /// <summary>Route name â†’ IRetriever. The LM's output (trimmed) selects a key.</summary>
    public required IReadOnlyDictionary<string, IRetriever> Routes { get; init; }

    /// <summary>System prompt instructing the LM to emit one route name verbatim.</summary>
    public required string SystemPrompt { get; init; }

    /// <summary>Fallback route key if the LM's choice is not in <see cref="Routes"/>. Default "default".</summary>
    public string DefaultRoute { get; init; } = "default";
}

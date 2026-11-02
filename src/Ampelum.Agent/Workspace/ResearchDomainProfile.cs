namespace SignalGrid.Ampelum.Agent;

/// <summary>Research domain — allows web search, web fetch, and read-only file access.
/// No local writes. Default DangerTier: Safe.
/// Ships with CitationCountGate + WordCountGate for quality assessment.</summary>
public sealed class ResearchDomainProfile : IDomainProfile
{
    public string Name => "research";

    public IReadOnlySet<string> ToolIds { get; } = new HashSet<string>
    {
        "WebSearch", "WebFetch", "FileRead",
    };

    public IReadOnlyList<string> RelevantSkillTags { get; } = new[]
        { "research", "summarize", "web-research" };

    public BlastRadiusScope BuildBlastRadius(string rootPath) =>
        new()
        {
            AllowedResourcePaths = new HashSet<string> { rootPath },
            MaxAllowedTier = DangerTier.Safe,
            AllowedDomains = new HashSet<string> { "*" },
        };

    /// <summary>Research domain registers citation-count and word-count gates.</summary>
    public IReadOnlyList<IQualityGate> Gates { get; } = ResearchGates.DefaultGates;
}

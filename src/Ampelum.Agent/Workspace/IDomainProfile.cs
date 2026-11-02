namespace SignalGrid.Ampelum.Agent;

/// <summary>Defines the tool set, skill tags, and blast radius defaults for a workspace domain.</summary>
public interface IDomainProfile
{
    string Name { get; }
    IReadOnlySet<string> ToolIds { get; }

    /// <summary>Skill tags relevant to this domain — used by ISkillSelector for fallback matching.</summary>
    IReadOnlyList<string> RelevantSkillTags { get; }

    /// <summary>Build a BlastRadiusScope with the workspace root path injected.
    /// Profiles declare defaults; the caller may only narrow, never widen.</summary>
    BlastRadiusScope BuildBlastRadius(string rootPath);

    /// <summary>Quality gates registered for this domain. Null/empty = no domain-specific gates
    /// (the default software gates from GateRunner apply).</summary>
    IReadOnlyList<IQualityGate>? Gates => null;

    /// <summary>Reviewers registered for this domain. Null/empty = no domain-specific reviewers.</summary>
    IReadOnlyList<ITaskReviewer>? Reviewers => null;
}

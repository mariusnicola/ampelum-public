using SignalGrid.Ampelum.Agent;

namespace Ampelum.Samples.AgentRuntime;

/// <summary>Registers the demo project as a workspace and derives the scopes the run works inside.</summary>
public static class TriageWorkspace
{
    /// <summary>Name the workspace is registered under.</summary>
    public const string Label = "Shop checkout";

    /// <summary>Id of the workspace in the registry.</summary>
    public static WorkspaceId Id { get; } = WorkspaceId.New(Label);

    /// <summary>
    /// Registers the project on first use and finds it again on later use. The registry file keeps label, root,
    /// weight, escalation mode, blast radius and allowed tools; the stores and skills are session-local and built fresh.
    /// </summary>
    public static WorkspaceContext Open(IProjectPortfolio registry, string root, TriageSkills skills)
    {
        var profile = new SoftwareDomainProfile();
        var stored = registry.Get(Id);
        if (stored is null)
        {
            stored = new WorkspaceContext
            {
                Id = Id,
                Label = Label,
                RootPath = root,
                MaxBlastRadius = profile.BuildBlastRadius(root),
                AllowedToolIds = new HashSet<string> { TriageTools.Audit, TriageTools.PostReport },
                TokenStore = new InMemoryTokenStore(),
                SecretStore = new InMemorySecretStore(),
                Skills = skills.Registry,
                EscalationPolicy = EscalationPolicy.Queue(),
            };
            registry.Register(stored);
        }

        // The blast radius and the allowed tools come from the registry file; only the session-local skills are attached.
        return stored with { Skills = skills.Registry };
    }

    /// <summary>
    /// The folders a task may read. It is built inside the workspace's blast radius and never wider; paths are
    /// normalised first so "../" cannot climb out, and a trailing separator keeps "src" from also admitting "src-old".
    /// </summary>
    public static BlastRadiusScope ReadableScope(WorkspaceContext workspace, TriagePolicy policy)
    {
        var folders = policy.ReadablePaths
            .Select(p => Path.GetFullPath(Path.Combine(workspace.RootPath, p)) + Path.DirectorySeparatorChar)
            .ToHashSet();
        // ContainsPath is a plain prefix test and the stored root has no trailing separator, so it also admits a
        // sibling such as "project-old". The root is compared here with its separator.
        var root = Path.GetFullPath(workspace.RootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var outside = folders.FirstOrDefault(f => !f.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !workspace.MaxBlastRadius.ContainsPath(f));
        if (outside is not null)
            throw new InvalidOperationException($"Readable path '{outside}' is outside the workspace blast radius.");
        return new BlastRadiusScope
        {
            AllowedResourcePaths = folders,
            AllowedToolNames = new HashSet<string> { TriageTools.Audit },
            MaxAllowedTier = DangerTier.Safe,
        };
    }
}

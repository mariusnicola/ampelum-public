namespace SignalGrid.Ampelum.Agent;

/// <summary>Software development domain — allows file system, shell, git, and SSH tools.
/// Network restricted to package registries. Default DangerTier: Destructive.</summary>
public sealed class SoftwareDomainProfile : IDomainProfile
{
    public string Name => "software";

    public IReadOnlySet<string> ToolIds { get; } = new HashSet<string>
    {
        "FileRead", "FileWrite", "FileEdit", "FileDelete", "FileMove",
        "ShellExec", "GitClone", "GitCommit", "GitPush",
        "SshCheckService", "SshRestartService", "SshGetUptime",
        "SshGetDiskUsage", "SshGetMemoryUsage", "SshDockerPs",
        "SshDockerPull", "SshUploadFile", "SshDownloadFile",
        "WebSearch", "WebFetch",
    };

    public IReadOnlyList<string> RelevantSkillTags { get; } = new[]
        { "coding", "git", "devops", "testing", "code-review" };

    public BlastRadiusScope BuildBlastRadius(string rootPath) =>
        new()
        {
            AllowedResourcePaths = new HashSet<string> { rootPath },
            MaxAllowedTier = DangerTier.Destructive,
        };
}
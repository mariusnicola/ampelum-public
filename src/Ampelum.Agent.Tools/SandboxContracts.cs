using System.Text.Json.Serialization;

namespace SignalGrid.Ampelum.Agent.Tools;

/// <summary>Executes a sandboxed command with the supplied policy.</summary>
public interface IExecutionSandbox
{
    ValueTask<SandboxResult> ExecuteAsync(
        SandboxCommand command,
        CancellationToken cancellationToken = default);
}

/// <summary>Describes the sandbox operation to run.</summary>
public sealed record SandboxCommand
{
    public SandboxCommandKind Kind { get; init; } = SandboxCommandKind.Process;

    public string Program { get; init; } = string.Empty;

    public string[] Arguments { get; init; } = Array.Empty<string>();

    public string? WorkingDirectory { get; init; }

    public string? StandardInput { get; init; }

    public SandboxPolicy Policy { get; init; } = new();

    public string? CorrelationId { get; init; }
}

/// <summary>Static policy describing what the sandbox may do.</summary>
public sealed record SandboxPolicy
{
    public SandboxIsolationMode IsolationMode { get; init; } = SandboxIsolationMode.Process;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    public bool AllowFileWrites { get; init; } = true;

    public bool PreserveEnvironment { get; init; } = true;

    public SandboxNetworkPolicy Network { get; init; } = new();

    public string[] AllowedWorkingDirectories { get; init; } = Array.Empty<string>();

    /// <summary>
    /// True when this policy asks for isolation guarantees — network access restricted to
    /// something less than <see cref="SandboxNetworkAccess.AllowAll"/>, or file writes
    /// restricted — that a sandbox backend must be able to enforce at the OS level before it
    /// may execute the command. Backends with no real isolation mechanism (e.g.
    /// <c>ProcessSandbox</c>) must refuse to run any command where this is true rather than
    /// silently executing it unrestricted.
    /// </summary>
    [JsonIgnore]
    public bool RequiresIsolation => Network.Access != SandboxNetworkAccess.AllowAll || !AllowFileWrites;
}

/// <summary>Network access policy for sandboxed execution.</summary>
public sealed record SandboxNetworkPolicy
{
    public SandboxNetworkAccess Access { get; init; } = SandboxNetworkAccess.AllowAll;

    public string[] AllowedHosts { get; init; } = Array.Empty<string>();

    public int[] AllowedPorts { get; init; } = Array.Empty<int>();
}

/// <summary>Result of a sandboxed command execution.</summary>
public sealed record SandboxResult
{
    public SandboxOutcome Outcome { get; init; } = SandboxOutcome.Failed;

    public int ExitCode { get; init; } = -1;

    public string StandardOutput { get; init; } = string.Empty;

    public string StandardError { get; init; } = string.Empty;

    public TimeSpan Duration { get; init; } = TimeSpan.Zero;

    public string? RejectionReason { get; init; }

    public string? CorrelationId { get; init; }
}

/// <summary>Command shape for the sandbox surface.</summary>
public enum SandboxCommandKind
{
    Process = 0,
    FileRead = 1,
    FileWrite = 2,
    FileDelete = 3,
}

/// <summary>Isolation level requested for a sandbox run.</summary>
public enum SandboxIsolationMode
{
    Process = 0,
    Container = 1,
    VirtualMachine = 2,
}

/// <summary>Network permission model for sandboxed execution.</summary>
public enum SandboxNetworkAccess
{
    DenyAll = 0,
    LoopbackOnly = 1,
    AllowList = 2,
    AllowAll = 3,
}

/// <summary>Execution outcome for a sandboxed command.</summary>
public enum SandboxOutcome
{
    Succeeded = 0,
    Failed = 1,
    Rejected = 2,
    TimedOut = 3,
    Cancelled = 4,
}

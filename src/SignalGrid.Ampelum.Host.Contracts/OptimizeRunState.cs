using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>Allowed values for <see cref="OptimizeRunStatus.State"/>. Spec §6 Default B.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public static class OptimizeRunState
{
    /// <summary>Run is waiting to be picked up.</summary>
    public const string Queued    = "queued";
    /// <summary>Run is actively executing.</summary>
    public const string Running   = "running";
    /// <summary>Run completed successfully.</summary>
    public const string Succeeded = "succeeded";
    /// <summary>Run terminated with an error.</summary>
    public const string Failed    = "failed";
    /// <summary>Run was cancelled by the caller.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>All allowed values in declaration order.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Queued, Running, Succeeded, Failed, Cancelled };
}

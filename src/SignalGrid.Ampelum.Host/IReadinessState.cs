#pragma warning disable AMPELUM_V09_PREVIEW
using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host;

/// <summary>
/// Tracks the readiness of the host. Used by the /readyz endpoint to determine
/// if the service can accept traffic. Marks the host as unready on split-brain detection.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IReadinessState
{
    /// <summary>True when the host is ready to serve requests.</summary>
    bool IsReady { get; }

    /// <summary>Human-readable reason why the host is unready; null when <see cref="IsReady"/> is true.</summary>
    string? UnreadyReason { get; }

    /// <summary>Marks the host as unready due to split-brain detection on a module version.</summary>
    void MarkSplitBrain(string moduleName, string versionId);
}

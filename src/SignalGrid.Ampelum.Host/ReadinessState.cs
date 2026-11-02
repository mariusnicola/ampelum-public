#pragma warning disable AMPELUM_V09_PREVIEW
using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host;

/// <summary>
/// Thread-safe implementation of <see cref="IReadinessState"/> using compare-and-exchange.
/// Once marked unready, the host remains unready (no reset).
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class ReadinessState : IReadinessState
{
    string? _splitBrain;

    /// <summary>True when the host is ready to serve requests.</summary>
    public bool IsReady => _splitBrain is null;

    /// <summary>Human-readable reason why the host is unready; null when <see cref="IsReady"/> is true.</summary>
    public string? UnreadyReason => _splitBrain;

    /// <summary>Marks the host as unready due to split-brain detection on a module version.</summary>
    public void MarkSplitBrain(string moduleName, string versionId) =>
        Interlocked.CompareExchange(ref _splitBrain, $"split_brain module={moduleName} version={versionId}", null);
}

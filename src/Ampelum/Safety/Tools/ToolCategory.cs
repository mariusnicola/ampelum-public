using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Safety.Tools;

/// <summary>Coarse-grained capability classification used by <see cref="ToolGatePolicy"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public enum ToolCategory
{
    /// <summary>Read-only information retrieval (search, fetch).</summary>
    Information,
    /// <summary>Pure computation, no side effects (calculator, code-eval).</summary>
    Computation,
    /// <summary>External communication side effects (email, slack).</summary>
    Communication,
    /// <summary>Read-side data access (db query, file read).</summary>
    DataAccess,
    /// <summary>Write-side data mutation (db write, file write).</summary>
    DataMutation,
    /// <summary>Process / shell / system control.</summary>
    SystemControl,
}

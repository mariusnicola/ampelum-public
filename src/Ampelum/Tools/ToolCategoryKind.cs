namespace SignalGrid.Ampelum.Tools;

/// <summary>Coarse classification of a tool's effects, used by future budget gating. PSQ-7.</summary>
public enum ToolCategoryKind
{
    /// <summary>Read-only operations (queries, lookups). No side effects.</summary>
    Read,
    /// <summary>Mutating operations on local state. Should also implement <see cref="IPreviewable"/>.</summary>
    Write,
    /// <summary>Pure computation. No I/O.</summary>
    Compute,
    /// <summary>Calls into external systems (HTTP, RPC). Side effects out of process.</summary>
    External
}

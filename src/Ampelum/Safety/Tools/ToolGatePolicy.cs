using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Safety.Tools;

/// <summary>Allow/deny lists for tool names + categories. <c>Deny</c> overrides <c>Allow</c>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record ToolGatePolicy(
    IReadOnlySet<string>? Allow = null,
    IReadOnlySet<string>? Deny = null,
    IReadOnlySet<ToolCategory>? AllowCategories = null,
    IReadOnlySet<ToolCategory>? DenyCategories = null)
{
    /// <summary>Empty policy â€” every tool is allowed.</summary>
    public static ToolGatePolicy Open { get; } = new();

    /// <summary>Returns true if the tool is allowed under this policy.</summary>
    public bool IsAllowed(string name, ToolCategory category)
    {
        if (Deny is not null && Deny.Contains(name)) return false;
        if (DenyCategories is not null && DenyCategories.Contains(category)) return false;
        if (Allow is not null && !Allow.Contains(name)) return false;
        if (AllowCategories is not null && !AllowCategories.Contains(category)) return false;
        return true;
    }
}

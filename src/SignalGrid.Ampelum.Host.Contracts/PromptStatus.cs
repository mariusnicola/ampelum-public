using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>Allowed values for <see cref="PromptVersion.Status"/>. Spec §6.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public static class PromptStatus
{
    /// <summary>Currently bound version for this module.</summary>
    public const string Active = "active";
    /// <summary>Persisted but not bound; awaiting promotion.</summary>
    public const string Challenger = "challenger";
    /// <summary>Previously active, replaced by promote.</summary>
    public const string Retired = "retired";
    /// <summary>All allowed values in declaration order.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Active, Challenger, Retired };
}

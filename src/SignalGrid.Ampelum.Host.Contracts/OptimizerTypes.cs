using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>
/// Known optimizer type names matching Ampelum core's registered optimizers.
/// Clients use these constants for autocomplete-safe optimizer selection.
/// New optimizers added to core require a one-line addition here. Spec §6 Default D.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public static class OptimizerTypes
{
    /// <summary>Bootstrap Few-Shot optimizer.</summary>
    public const string BootstrapFewShot = "BootstrapFewShot";
    /// <summary>MIPROv2 multi-prompt optimizer.</summary>
    public const string MIPROv2         = "MIPROv2";
    /// <summary>COPRO collaborative-prompt optimizer.</summary>
    public const string COPRO           = "COPRO";
    /// <summary>SIMBA stochastic optimizer.</summary>
    public const string SIMBA           = "SIMBA";

    /// <summary>All known optimizer types in declaration order.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { BootstrapFewShot, MIPROv2, COPRO, SIMBA };
}

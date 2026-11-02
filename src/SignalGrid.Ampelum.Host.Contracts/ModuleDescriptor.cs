using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>Describes a registered module exposed via <c>GET /modules/{moduleName}</c>. Spec §6.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record ModuleDescriptor(
    string Name,
    string SignatureType,
    string Description,
    IReadOnlyList<FieldDescriptor> Inputs,
    IReadOnlyList<FieldDescriptor> Outputs,
    bool SupportsStreaming);

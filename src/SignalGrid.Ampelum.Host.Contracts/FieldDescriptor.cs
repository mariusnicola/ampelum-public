using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>Describes a single input or output field in a module signature. Spec §6.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record FieldDescriptor(
    string Name,
    string ClrType,
    string? Description,
    bool Required);

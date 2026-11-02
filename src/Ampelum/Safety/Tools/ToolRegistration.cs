using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Safety.Tools;

/// <summary>A single (Name, Version) tool registration in <see cref="IToolRegistry"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record ToolRegistration(
    string Name,
    string Version,
    ToolCategory Category,
    string? Description = null,
    IReadOnlyDictionary<string, object?>? Metadata = null);

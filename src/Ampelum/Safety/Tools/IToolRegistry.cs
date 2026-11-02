using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Safety.Tools;

/// <summary>Source-of-truth for all tools the consumer has declared.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IToolRegistry
{
    /// <summary>Looks up by exact (name, version) tuple.</summary>
    bool TryGet(string name, string version, out ToolRegistration? registration);

    /// <summary>Enumerates all registrations.</summary>
    IEnumerable<ToolRegistration> GetAll();
}

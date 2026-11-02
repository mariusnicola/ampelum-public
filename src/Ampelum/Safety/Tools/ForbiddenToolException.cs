using System;
using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Safety.Tools;

/// <summary>Thrown when <see cref="ToolGateHook"/> denies a tool invocation under the active policy.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class ForbiddenToolException : Exception
{
    /// <summary>Builds the exception around the denied tool's identity.</summary>
    public ForbiddenToolException(string toolName, string toolVersion, string reason)
        : base($"Tool '{toolName}'@'{toolVersion}' was denied by policy: {reason}.")
    {
        ToolName = toolName;
        ToolVersion = toolVersion;
        Reason = reason;
    }

    /// <summary>The tool that was denied.</summary>
    public string ToolName { get; }
    /// <summary>The tool version that was denied.</summary>
    public string ToolVersion { get; }
    /// <summary>Human-readable reason for the denial.</summary>
    public string Reason { get; }
}

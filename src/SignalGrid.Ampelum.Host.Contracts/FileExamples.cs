using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>Host-readable filesystem path containing newline-delimited <see cref="DemoExample"/> JSON. Spec §6.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record FileExamples(string Path) : ExampleSource;

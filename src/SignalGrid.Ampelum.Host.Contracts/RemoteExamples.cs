using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>Remote URI (http(s) or file://). Optional <see cref="Sha256"/> integrity check.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record RemoteExamples(string Uri, string? Sha256 = null) : ExampleSource;

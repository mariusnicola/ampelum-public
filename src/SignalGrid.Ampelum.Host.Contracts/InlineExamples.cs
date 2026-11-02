using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>Inline examples. Bytes-on-wire scales with <see cref="Examples"/> length.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record InlineExamples(IReadOnlyList<DemoExample> Examples) : ExampleSource;

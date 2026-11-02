using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>A single demonstration example used in prompt versions and optimizer training sets. Spec §6.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record DemoExample(
    IReadOnlyDictionary<string, JsonElement> Inputs,
    IReadOnlyDictionary<string, JsonElement> Outputs);

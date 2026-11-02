using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Retrieval.Loaders;

/// <summary>A loaded document (single page, section, or whole-file unit depending on loader).</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record Document(string Id, string Text, IReadOnlyDictionary<string, object?> Metadata);

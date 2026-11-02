using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Retrieval.Splitters;

/// <summary>A piece of text produced by an <see cref="ITextSplitter"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record TextChunk(
    string Text,
    int StartOffset,
    int EndOffset,
    IReadOnlyDictionary<string, object?> Metadata);

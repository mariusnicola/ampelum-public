using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Retrieval.Splitters;

/// <summary>Splits a text into <see cref="TextChunk"/> records. Implementations SHOULD emit a span on <c>Diagnostics.Source</c> named <c>ampelum.splitter.split</c>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface ITextSplitter
{
    /// <summary>Splits text into chunks per <paramref name="context"/>.</summary>
    IEnumerable<TextChunk> Split(string text, SplitterContext context);
}

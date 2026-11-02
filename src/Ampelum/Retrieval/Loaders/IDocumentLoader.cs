using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Retrieval.Loaders;

/// <summary>Loads a stream into one-or-more <see cref="Document"/> records.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IDocumentLoader
{
    /// <summary>Streams documents from the source. Implementations SHOULD emit a span on <c>Diagnostics.Source</c> named <c>ampelum.loader.load</c>.</summary>
    IAsyncEnumerable<Document> LoadAsync(Stream source, DocumentLoadContext context, CancellationToken ct = default);
}

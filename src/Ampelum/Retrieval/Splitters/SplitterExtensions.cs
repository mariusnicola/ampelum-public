using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.Retrieval.Loaders;

namespace SignalGrid.Ampelum.Retrieval.Splitters;

/// <summary>Extension helpers connecting <see cref="ITextSplitter"/> with loader <see cref="Document"/> streams.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public static class SplitterExtensions
{
    /// <summary>Splits each loaded document into chunked sub-documents (id format <c>{sourceId}#{startOffset}-{endOffset}</c>).</summary>
    public static IEnumerable<Document> SplitDocuments(this ITextSplitter splitter, IEnumerable<Document> docs, SplitterContext baseContext)
    {
        foreach (var doc in docs)
        {
            var ctx = new SplitterContext
            {
                ChunkSize = baseContext.ChunkSize,
                ChunkOverlap = baseContext.ChunkOverlap,
                SourceDocumentId = doc.Id,
            };
            foreach (var c in splitter.Split(doc.Text, ctx))
            {
                var meta = new Dictionary<string, object?>(doc.Metadata)
                {
                    ["chunk_start"] = c.StartOffset,
                    ["chunk_end"] = c.EndOffset,
                };
                yield return new Document($"{doc.Id}#{c.StartOffset}-{c.EndOffset}", c.Text, meta);
            }
        }
    }
}

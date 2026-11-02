using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using UglyToad.PdfPig;

namespace SignalGrid.Ampelum.Retrieval.Loaders;

/// <summary>Loads a PDF stream into one <see cref="Document"/> per page using PdfPig (Apache-2.0).</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class PdfLoader : IDocumentLoader
{
    /// <inheritdoc />
    public async IAsyncEnumerable<Document> LoadAsync(
        Stream source,
        DocumentLoadContext context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.loader.load");
        activity?.SetTag("SignalGrid.Ampelum.loader.kind", "pdf");

        if (context.MaxDocumentBytes is long limit && source.CanSeek && source.Length > limit)
        {
            throw new LoaderBudgetExceededException(limit, source.Length);
        }

        PdfDocument? doc = null;
        try
        {
            doc = PdfDocument.Open(source);
        }
        catch
        {
            yield break;
        }

        try
        {
            int pageNum = 0;
            foreach (var page in doc.GetPages())
            {
                pageNum++;
                ct.ThrowIfCancellationRequested();
                var meta = new Dictionary<string, object?> { ["page"] = pageNum };
                if (context.SourceUri is { } uri)
                {
                    meta["source"] = uri;
                }
                var id = context.SourceUri is null ? $"page={pageNum}" : $"{context.SourceUri}#page={pageNum}";
                yield return new Document(id, page.Text, meta);
                await Task.Yield();
            }
        }
        finally
        {
            doc.Dispose();
        }
    }
}

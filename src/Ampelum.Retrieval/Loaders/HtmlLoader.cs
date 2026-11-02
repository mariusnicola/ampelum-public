using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using AngleSharp;
using AngleSharp.Dom;

namespace SignalGrid.Ampelum.Retrieval.Loaders;

/// <summary>Loads HTML using AngleSharp (MIT, WHATWG-DOM-conformant). Strips script/style; emits one Document with heading metadata.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class HtmlLoader : IDocumentLoader
{
    /// <inheritdoc />
    public async IAsyncEnumerable<Document> LoadAsync(
        Stream source,
        DocumentLoadContext context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.loader.load");
        activity?.SetTag("SignalGrid.Ampelum.loader.kind", "html");

        var config = Configuration.Default;
        var browsingContext = BrowsingContext.New(config);
        using var doc = await browsingContext.OpenAsync(req => req.Content(source), ct).ConfigureAwait(false);

        foreach (var n in doc.QuerySelectorAll("script,style,noscript"))
        {
            n.Remove();
        }

        var headings = doc.QuerySelectorAll("h1,h2,h3,h4,h5,h6").Select(h => h.TextContent.Trim()).ToList();
        var text = doc.Body?.TextContent.Trim() ?? string.Empty;
        var meta = new Dictionary<string, object?> { ["headings"] = (IReadOnlyList<string>)headings };
        if (context.SourceUri is { } uri)
        {
            meta["source"] = uri;
        }
        var id = context.SourceUri ?? "html";
        yield return new Document(id, text, meta);
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace SignalGrid.Ampelum.Retrieval.Loaders;

/// <summary>Loads a Markdown stream using Markdig (BSD-2). Emits one Document with heading metadata extracted from the AST.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class MarkdownLoader : IDocumentLoader
{
    /// <inheritdoc />
    public async IAsyncEnumerable<Document> LoadAsync(
        Stream source,
        DocumentLoadContext context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.loader.load");
        activity?.SetTag("SignalGrid.Ampelum.loader.kind", "markdown");

        using var reader = new StreamReader(source, context.Encoding);
        var raw = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        var ast = Markdown.Parse(raw);
        var headings = new List<string>();
        foreach (var heading in ast.Descendants<HeadingBlock>())
        {
            var text = string.Concat(heading.Inline?.Descendants<LiteralInline>().Select(l => l.Content.ToString()) ?? Array.Empty<string>());
            headings.Add(text);
        }
        var meta = new Dictionary<string, object?> { ["headings"] = (IReadOnlyList<string>)headings };
        if (context.SourceUri is { } uri)
        {
            meta["source"] = uri;
        }
        yield return new Document(context.SourceUri ?? "md", raw, meta);
    }
}

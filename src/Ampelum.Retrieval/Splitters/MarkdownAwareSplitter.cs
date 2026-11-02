using System.Diagnostics.CodeAnalysis;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace SignalGrid.Ampelum.Retrieval.Splitters;

/// <summary>Markdown-aware splitter: chunks on heading boundaries and tags each chunk with its heading path.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class MarkdownAwareSplitter : ITextSplitter
{
    /// <inheritdoc />
    public IEnumerable<TextChunk> Split(string text, SplitterContext context)
    {
        using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.splitter.split");
        activity?.SetTag("SignalGrid.Ampelum.splitter.kind", "markdown");

        var ast = Markdown.Parse(text);
        var headings = ast.Descendants<HeadingBlock>().ToList();
        if (headings.Count == 0)
        {
            yield return new TextChunk(text, 0, text.Length, new Dictionary<string, object?>());
            yield break;
        }

        var path = new List<(int Level, string Title)>();
        for (int i = 0; i < headings.Count; i++)
        {
            var h = headings[i];
            var title = string.Concat(h.Inline?.Descendants<LiteralInline>().Select(l => l.Content.ToString()) ?? Array.Empty<string>());
            while (path.Count > 0 && path[^1].Level >= h.Level)
            {
                path.RemoveAt(path.Count - 1);
            }
            path.Add((h.Level, title));
            int sectionStart = h.Span.Start;
            int sectionEnd = i + 1 < headings.Count ? headings[i + 1].Span.Start : text.Length;
            var slice = text.Substring(sectionStart, sectionEnd - sectionStart);
            var headingPath = string.Join(" > ", path.Select(p => p.Title));
            yield return new TextChunk(slice, sectionStart, sectionEnd, new Dictionary<string, object?> { ["heading_path"] = headingPath });
        }
    }
}

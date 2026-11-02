using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace SignalGrid.Ampelum.Retrieval.Splitters;

/// <summary>Recursive character splitter (LangChain RCTS port). Tries successively-finer separators to keep chunks under <see cref="SplitterContext.ChunkSize"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class RecursiveCharacterSplitter(IReadOnlyList<string>? separators = null) : ITextSplitter
{
    private readonly IReadOnlyList<string> _separators = separators ?? ["\n\n", "\n", ". ", " ", ""];

    /// <inheritdoc />
    public IEnumerable<TextChunk> Split(string text, SplitterContext context)
    {
        using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.splitter.split");
        activity?.SetTag("SignalGrid.Ampelum.splitter.kind", "recursive");
        if (text.Length <= context.ChunkSize)
        {
            yield return new TextChunk(text, 0, text.Length, new Dictionary<string, object?>());
            yield break;
        }
        foreach (var chunk in SplitInternal(text, 0, context))
        {
            yield return chunk;
        }
    }

    private IEnumerable<TextChunk> SplitInternal(string text, int absoluteStart, SplitterContext ctx)
    {
        string sep = _separators[^1];
        foreach (var s in _separators)
        {
            if (s.Length > 0 && text.IndexOf(s, StringComparison.Ordinal) >= 0)
            {
                sep = s;
                break;
            }
        }

        if (sep.Length == 0)
        {
            int pos = 0;
            while (pos < text.Length)
            {
                int len = Math.Min(ctx.ChunkSize, text.Length - pos);
                yield return new TextChunk(text.Substring(pos, len), absoluteStart + pos, absoluteStart + pos + len, new Dictionary<string, object?>());
                if (pos + len >= text.Length)
                    break;
                pos += Math.Max(1, ctx.ChunkSize - ctx.ChunkOverlap);
            }
            yield break;
        }

        var parts = text.Split(sep);
        var buffer = new StringBuilder();
        int bufferStart = 0;
        int cursor = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            var addition = (buffer.Length == 0 ? "" : sep) + parts[i];
            if (buffer.Length + addition.Length > ctx.ChunkSize && buffer.Length > 0)
            {
                var s = buffer.ToString();
                if (s.Length > ctx.ChunkSize)
                {
                    foreach (var sub in SplitInternal(s, absoluteStart + bufferStart, ctx))
                    {
                        yield return sub;
                    }
                }
                else
                {
                    yield return new TextChunk(s, absoluteStart + bufferStart, absoluteStart + bufferStart + s.Length, new Dictionary<string, object?>());
                }
                int ov = Math.Min(ctx.ChunkOverlap, s.Length);
                buffer.Clear();
                buffer.Append(s[^ov..]);
                bufferStart = cursor - ov;
            }
            if (buffer.Length > 0 && i > 0)
            {
                buffer.Append(sep);
                cursor += sep.Length;
            }
            buffer.Append(parts[i]);
            cursor += parts[i].Length;
        }
        if (buffer.Length > 0)
        {
            var s = buffer.ToString();
            yield return new TextChunk(s, absoluteStart + bufferStart, absoluteStart + bufferStart + s.Length, new Dictionary<string, object?>());
        }
    }
}

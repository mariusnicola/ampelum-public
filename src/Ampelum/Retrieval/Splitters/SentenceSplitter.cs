using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;

namespace SignalGrid.Ampelum.Retrieval.Splitters;

/// <summary>Sentence-boundary splitter using a generated regex (preserves sentence integrity within chunk-size budget).</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed partial class SentenceSplitter : ITextSplitter
{
    [GeneratedRegex(@"(?<=[\.!?])\s+", RegexOptions.Compiled)]
    private static partial Regex Boundary();

    /// <inheritdoc />
    public IEnumerable<TextChunk> Split(string text, SplitterContext context)
    {
        using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.splitter.split");
        activity?.SetTag("SignalGrid.Ampelum.splitter.kind", "sentence");

        var sentences = Boundary().Split(text);
        var buffer = new StringBuilder();
        int bufStart = 0;
        int offset = 0;
        foreach (var s in sentences)
        {
            if (buffer.Length + s.Length > context.ChunkSize && buffer.Length > 0)
            {
                var content = buffer.ToString().TrimEnd();
                yield return new TextChunk(content, bufStart, bufStart + content.Length, new Dictionary<string, object?>());
                buffer.Clear();
                bufStart = offset;
            }
            buffer.Append(s);
            buffer.Append(' ');
            offset += s.Length + 1;
        }
        if (buffer.Length > 0)
        {
            var content = buffer.ToString().TrimEnd();
            yield return new TextChunk(content, bufStart, bufStart + content.Length, new Dictionary<string, object?>());
        }
    }
}

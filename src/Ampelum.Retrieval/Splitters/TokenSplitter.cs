using System.Diagnostics.CodeAnalysis;
using SharpToken;

namespace SignalGrid.Ampelum.Retrieval.Splitters;

/// <summary>Splits by GPT token count using SharpToken (cl100k_base by default).</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class TokenSplitter(string encodingName = "cl100k_base") : ITextSplitter
{
    private readonly GptEncoding _encoder = GptEncoding.GetEncoding(encodingName);

    /// <inheritdoc />
    public IEnumerable<TextChunk> Split(string text, SplitterContext context)
    {
        using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.splitter.split");
        activity?.SetTag("SignalGrid.Ampelum.splitter.kind", "token");

        var tokens = _encoder.Encode(text);
        if (tokens.Count <= context.ChunkSize)
        {
            yield return new TextChunk(text, 0, text.Length, new Dictionary<string, object?> { ["token_count"] = tokens.Count });
            yield break;
        }
        int start = 0;
        while (start < tokens.Count)
        {
            int len = Math.Min(context.ChunkSize, tokens.Count - start);
            var slice = tokens.GetRange(start, len);
            var decoded = _encoder.Decode(slice);
            int charStart = _encoder.Decode(tokens.GetRange(0, start)).Length;
            yield return new TextChunk(decoded, charStart, charStart + decoded.Length, new Dictionary<string, object?> { ["token_count"] = len });
            if (start + len == tokens.Count)
                break;
            start += Math.Max(1, context.ChunkSize - context.ChunkOverlap);
        }
    }
}

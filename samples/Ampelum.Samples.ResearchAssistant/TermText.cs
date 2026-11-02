using System.Text.RegularExpressions;

namespace Ampelum.Samples.ResearchAssistant;

/// <summary>Shared word normalisation so the embedder and the reranker agree on what a term is.</summary>
public static partial class TermText
{
    static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "of", "to", "in", "on", "and", "or", "is", "are", "it", "that", "how", "much", "many",
        "does", "do", "would", "will", "for", "with", "by", "at", "as", "be", "than", "then", "so", "its", "per",
    };

    [GeneratedRegex("[a-z0-9]+")]
    private static partial Regex Word();

    /// <summary>Lower case words without stop words, with a trailing plural s removed.</summary>
    public static List<string> Terms(string text)
    {
        var terms = new List<string>();
        foreach (Match m in Word().Matches(text.ToLowerInvariant()))
        {
            var w = m.Value;
            if (Stop.Contains(w))
                continue;
            if (w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal))
                w = w[..^1];
            terms.Add(w);
        }
        return terms;
    }
}

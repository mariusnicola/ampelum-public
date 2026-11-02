using SharpToken;

namespace SignalGrid.Ampelum.Context.Selection;

/// <summary>Token counter using cl100k_base encoding (same as GPT-4).</summary>
public static class Cl100kTokenCounter
{
    private static readonly GptEncoding Encoding = GptEncoding.GetEncoding("cl100k_base");

    /// <summary>Counts the number of tokens in the given text using cl100k_base encoding.</summary>
    public static int Count(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return Encoding.CountTokens(text);
    }
}

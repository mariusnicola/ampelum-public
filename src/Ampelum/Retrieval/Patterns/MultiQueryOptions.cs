using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Retrieval.Patterns;

/// <summary>Options for <see cref="MultiQueryRetriever"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class MultiQueryOptions
{
    /// <summary>Number of paraphrases to ask the LM for. Default 3.</summary>
    public int NumParaphrases { get; init; } = 3;

    /// <summary>System prompt template; <c>{N}</c> is replaced with <see cref="NumParaphrases"/>.</summary>
    public string SystemPrompt { get; init; } =
        "Generate {N} paraphrases of the user's query, one per line. Preserve intent.";
}

using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Retrieval.Patterns;

/// <summary>Options for <see cref="SubQuestionRetriever"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class SubQuestionOptions
{
    /// <summary>Maximum sub-questions to decompose into. Default 4.</summary>
    public int MaxSubQuestions { get; init; } = 4;

    /// <summary>System prompt template; <c>{N}</c> is replaced with <see cref="MaxSubQuestions"/>.</summary>
    public string SystemPrompt { get; init; } =
        "Decompose the user's question into AT MOST {N} sub-questions, one per line, no numbering.";
}

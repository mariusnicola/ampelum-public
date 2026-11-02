using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Retrieval.Patterns;

/// <summary>Options for <see cref="HydeRetriever"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class HydeOptions
{
    /// <summary>Toggle. When false, falls through to inner retriever (no hypothesis generation).</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>Maximum hypothesis length in tokens. Default 256.</summary>
    public int HypothesisTokens { get; init; } = 256;

    /// <summary>System prompt steering hypothesis generation.</summary>
    public string SystemPrompt { get; init; } =
        "Write a concise hypothetical answer to the user's question, as if you had retrieved the perfect document. Output only the answer text.";

    /// <summary>Optional override <see cref="ChatOptions"/> for the hypothesis call.</summary>
    public ChatOptions? ChatOptions { get; init; }
}

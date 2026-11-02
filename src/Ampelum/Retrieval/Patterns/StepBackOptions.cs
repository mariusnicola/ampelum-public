using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Retrieval.Patterns;

/// <summary>Options for <see cref="StepBackRetriever"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class StepBackOptions
{
    /// <summary>System prompt that asks the LM to take a "step back" â€” abstract the question.</summary>
    public string SystemPrompt { get; init; } =
        "Rewrite the user's specific question as a more abstract, higher-level question that retrieves background context.";

    /// <summary>If true, retrieves with both the original and the abstracted query and fuses (default true).</summary>
    public bool IncludeOriginal { get; init; } = true;
}

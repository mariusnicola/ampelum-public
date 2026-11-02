using SignalGrid.Ampelum.Agents;

namespace Ampelum.Samples.ResearchAssistant;

/// <summary>One tool invocation made by the analyst, with its outcome.</summary>
public sealed record ToolCall(string Name, string Argument, string? Output, string? Error);

/// <summary>Everything one question produced, including which sources the answer may cite.</summary>
public sealed record ResearchResult(
    string Question,
    string Answer,
    IReadOnlyList<string> Speakers,
    TerminationReason Termination,
    IReadOnlyList<string> Queries,
    IReadOnlyList<string> PassageIds,
    IReadOnlyList<ToolCall> ToolCalls,
    IReadOnlyList<string> Citations,
    IReadOnlyList<string> UnknownCitations)
{
    /// <summary>An answer counts as grounded when it cites at least one source and every citation exists.</summary>
    public bool IsGrounded => Citations.Count > 0 && UnknownCitations.Count == 0;
}

/// <summary>Tunable limits of the pipeline; the model and keys are never part of it.</summary>
public sealed class ResearchOptions
{
    public int ChunkSize { get; init; } = 300;
    public int ChunkOverlap { get; init; } = 40;
    public int PassagesPerQuery { get; init; } = 2;
    public int CandidatePool { get; init; } = 6;
    public int SearchTopK { get; init; } = 2;
    public int MaxToolIterations { get; init; } = 8;
    public int MemoryCapacity { get; init; } = 16;
    public int MaxTurns { get; init; } = 8;
}

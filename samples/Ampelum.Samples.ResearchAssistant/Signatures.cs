using SignalGrid.Ampelum;

namespace Ampelum.Samples.ResearchAssistant;

/// <summary>Instruction texts; the fake model routes on them, so they live in one place.</summary>
public static class Prompts
{
    public const string Plan = "Split the research question into short document search queries, separated by semicolons.";
    public const string Analyse = "Answer the question from the passages. Use the tools for arithmetic, definitions and fresh facts, and mention the source id of every fact.";
    public const string Write = "Write the final answer from the findings. Cite each fact with the source id in square brackets, for example [doc#1]. Cite only ids that appear in the findings or passages.";
}

[Signature(Prompts.Plan)]
public sealed record PlanSignature : ISignature
{
    [Input] public required string Question { get; init; }
    [Output] public string Queries { get; init; } = "";
}

/// <summary>ReAct allows exactly one output field.</summary>
[Signature(Prompts.Analyse)]
public sealed record AnalystSignature : ISignature
{
    [Input] public required string Question { get; init; }
    [Input] public required string Passages { get; init; }
    [Output] public string Findings { get; init; } = "";
}

[Signature(Prompts.Write)]
public sealed record WriteSignature : ISignature
{
    [Input] public required string Question { get; init; }
    [Input] public required string Findings { get; init; }
    [Input] public required string Passages { get; init; }
    [Output] public string Answer { get; init; } = "";
}

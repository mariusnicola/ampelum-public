using SignalGrid.Ampelum;

namespace Ampelum.Samples.AgentRuntime;

/// <summary>Model answer for one issue.</summary>
[Signature("Assess the issue from the brief. Severity is low, medium or high. Cause names the file and method most likely at fault.")]
public sealed record IssueAssessment : ISignature
{
    [Input] public required string Brief { get; init; }
    [Output] public string Severity { get; init; } = "";
    [Output] public string Cause { get; init; } = "";
    [Output] public string NextStep { get; init; } = "";
}

/// <summary>Model verdict on the report draft. Verdict is revise or ok; Note is one line for the editor log.</summary>
[Signature("Refine the report draft. Answer revise when it should change again, ok when it is final.")]
public sealed record ReportRefinement : ISignature
{
    [Input] public required string Draft { get; init; }
    [Input] public required string Context { get; init; }
    [Output] public string Verdict { get; init; } = "";
    [Output] public string Note { get; init; } = "";
}

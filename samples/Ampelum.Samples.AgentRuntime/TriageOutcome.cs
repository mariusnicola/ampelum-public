using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Agent.Contracts;
using SignalGrid.Ampelum.Agent.Orchestration;

namespace Ampelum.Samples.AgentRuntime;

/// <summary>What the fan-out step stored in its checkpoint, so later steps and a restarted process can read it.</summary>
public sealed record AssessDetail(IReadOnlyList<IssueResult> Results, IReadOnlyList<DeferredIssue> Deferred, double Score);

/// <summary>Everything a finished run left behind, read back from the stores and not from memory.</summary>
public sealed record TriageOutcome(
    WorkflowExecutionStatus Status,
    string? FailureReason,
    AssessDetail Assessed,
    IReadOnlyList<string> EditorNotes,
    string? ReportPath,
    IReadOnlyList<WorkflowRuntimeAuditEvent> WorkflowAudit,
    IReadOnlyList<DurableRunEvent> RunEvents,
    WorkflowHealth Health);

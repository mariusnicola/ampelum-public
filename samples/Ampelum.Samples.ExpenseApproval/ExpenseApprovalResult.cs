using SignalGrid.Ampelum.Agent.Contracts;
using SignalGrid.Ampelum.Agent.Orchestration;

namespace Ampelum.Samples.ExpenseApproval;

/// <summary>Outcome of one expense approval execution.</summary>
/// <param name="ExecutionId">Durable execution identifier.</param>
/// <param name="Status">Final execution status.</param>
/// <param name="Expense">The expense extracted by the model, or null if extraction did not finish.</param>
/// <param name="ToolExecuted">Name of the tool step that ran (pay or flag for review), or null if none succeeded.</param>
/// <param name="FailureReason">Why the execution failed, when it did.</param>
/// <param name="Audit">Runtime audit history read back from the persistence the run finished on.</param>
public sealed record ExpenseApprovalResult(
    string ExecutionId,
    WorkflowExecutionStatus Status,
    Expense? Expense,
    string? ToolExecuted,
    string? FailureReason,
    IReadOnlyList<WorkflowRuntimeAuditEvent> Audit);

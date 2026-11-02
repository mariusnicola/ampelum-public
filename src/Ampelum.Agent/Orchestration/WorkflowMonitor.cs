using SignalGrid.Ampelum.Agent.Contracts;

namespace SignalGrid.Ampelum.Agent.Orchestration;

/// <summary>Reads recorded <see cref="OutcomeRecord"/>s for a workflow and produces a <see cref="WorkflowHealth"/> summary.</summary>
public interface IWorkflowMonitor
{
    /// <summary>Evaluates <paramref name="outcomes"/> into a health summary for <paramref name="workflowId"/>.</summary>
    Task<WorkflowHealth> EvaluateAsync(string workflowId, IReadOnlyList<OutcomeRecord> outcomes, CancellationToken ct = default);
}

/// <summary>
/// Deterministic, LLM-free <see cref="IWorkflowMonitor"/>: aggregates success rate, average cost, and average
/// latency directly from recorded outcomes, and flags a small set of concrete concerns (a declining success
/// rate across the sample, or a cost/latency outlier well above the sample average).
/// </summary>
public sealed class DefaultWorkflowMonitor : IWorkflowMonitor
{
    /// <inheritdoc />
    public Task<WorkflowHealth> EvaluateAsync(string workflowId, IReadOnlyList<OutcomeRecord> outcomes, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        ArgumentNullException.ThrowIfNull(outcomes);
        ct.ThrowIfCancellationRequested();

        if (outcomes.Count == 0)
            return Task.FromResult(new WorkflowHealth(workflowId, DateTimeOffset.UtcNow, 0, 0, Concerns: ["No outcomes recorded yet."]));

        // Success rate is defined only over terminal outcomes -- a Queued/Running/AwaitingConfirmation entry
        // is neither a success nor a failure, and folding it into the denominator (or worse, the "not
        // succeeded" bucket) would misrepresent health exactly the way this workstream exists to prevent for
        // evidence and budget. Non-terminal entries are excluded from the rate and called out as a concern.
        var terminal = outcomes.Where(o => o.Result is WorkflowExecutionStatus.Succeeded or WorkflowExecutionStatus.Failed or WorkflowExecutionStatus.Cancelled).ToArray();
        var succeeded = terminal.Count(o => o.Result == WorkflowExecutionStatus.Succeeded);
        var successRate = terminal.Length > 0 ? (double)succeeded / terminal.Length : 0;

        var costs = outcomes.Where(o => o.Cost is not null).Select(o => o.Cost!.Value).ToArray();
        var averageCost = costs.Length > 0 ? costs.Average() : (decimal?)null;

        var latencies = outcomes.Where(o => o.Latency is not null).Select(o => o.Latency!.Value).ToArray();
        var averageLatency = latencies.Length > 0 ? TimeSpan.FromTicks((long)latencies.Average(l => l.Ticks)) : (TimeSpan?)null;

        var concerns = new List<string>();
        var nonTerminalCount = outcomes.Count - terminal.Length;
        if (nonTerminalCount > 0)
            concerns.Add($"{nonTerminalCount} of {outcomes.Count} sampled outcomes were not terminal (Queued/Running/AwaitingConfirmation) and were excluded from the success-rate calculation.");
        if (terminal.Length > 0 && successRate < 0.5)
            concerns.Add($"Success rate {successRate:P0} is below 50% across {terminal.Length} terminal sampled executions.");
        if (outcomes.Any(o => o.RiskEvents is { Count: > 0 }))
            concerns.Add($"{outcomes.Count(o => o.RiskEvents is { Count: > 0 })} of {outcomes.Count} sampled executions recorded a risk-constraint breach.");

        return Task.FromResult(new WorkflowHealth(
            workflowId,
            DateTimeOffset.UtcNow,
            outcomes.Count,
            successRate,
            averageCost,
            averageLatency,
            concerns.Count > 0 ? concerns : null));
    }
}

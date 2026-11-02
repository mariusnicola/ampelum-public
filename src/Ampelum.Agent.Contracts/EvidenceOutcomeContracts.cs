using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Agent.Contracts;

/// <summary>
/// One piece of external evidence a workflow's context requests may resolve to. Distinct from a raw tool
/// result: an evidence record carries its own provenance, validity window, and access scope, so a consumer
/// (e.g. a prompt context manifest) can decide whether it's still usable and who may see it, independent of
/// whoever originally captured it.
/// </summary>
/// <param name="EvidenceId">Stable identifier for this evidence record.</param>
/// <param name="Source">Identifier of the system or tool that produced this evidence.</param>
/// <param name="SourceVersion">Version of the source at capture time, if known.</param>
/// <param name="CapturedAt">When this evidence was captured.</param>
/// <param name="ValidUntil">When this evidence stops being usable, if it expires.</param>
/// <param name="Scope">Identifier of the scope (e.g. workspace, tenant) this evidence is visible within.</param>
/// <param name="Permissions">Identifiers of the permissions required to read this evidence's content.</param>
/// <param name="Confidence">Optional confidence score in [0, 1] for this evidence's accuracy.</param>
/// <param name="ContentHash">Content hash of the evidence payload, for integrity and deduplication — the payload itself is not part of this record.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record EvidenceRecord(
    string EvidenceId,
    string Source,
    DateTimeOffset CapturedAt,
    string ContentHash,
    string? SourceVersion = null,
    DateTimeOffset? ValidUntil = null,
    string? Scope = null,
    IReadOnlyList<string>? Permissions = null,
    double? Confidence = null);

/// <summary>Proof that a step or action actually consumed a specific piece of evidence.</summary>
/// <param name="EvidenceId">The evidence record consumed.</param>
/// <param name="ConsumedByStepId">The workflow step that consumed it.</param>
/// <param name="ConsumedAt">When it was consumed.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record EvidenceReceipt(string EvidenceId, string ConsumedByStepId, DateTimeOffset ConsumedAt);

/// <summary>One observed value of a named metric.</summary>
/// <param name="MetricId">Identifier of the metric, matching a <see cref="WorkflowObjectiveMetric.MetricId"/>.</param>
/// <param name="Value">The observed value.</param>
/// <param name="ObservedAt">When this value was observed.</param>
/// <param name="Source">Identifier of what produced this observation.</param>
/// <param name="Unit">Unit of measurement, if applicable.</param>
/// <param name="Confidence">Optional confidence score in [0, 1] for this observation.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record MetricObservation(
    string MetricId,
    double Value,
    DateTimeOffset ObservedAt,
    string Source,
    string? Unit = null,
    double? Confidence = null);

/// <summary>Severity of a declared risk constraint.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public enum RiskSeverity
{
    /// <summary>A breach is worth recording but not acting on.</summary>
    Low,
    /// <summary>A breach warrants attention but not necessarily intervention.</summary>
    Medium,
    /// <summary>A breach warrants intervention.</summary>
    High,
    /// <summary>A breach demands immediate intervention.</summary>
    Critical,
}

/// <summary>What happens when a <see cref="RiskConstraint"/>'s limit is breached.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public enum RiskBreachAction
{
    /// <summary>Record the breach; take no other action.</summary>
    Warn,
    /// <summary>Block further progress.</summary>
    Block,
    /// <summary>Require an authenticated approval before further progress.</summary>
    RequireApproval,
    /// <summary>Halt the entire execution immediately.</summary>
    Halt,
}

/// <summary>A single named risk limit and what to do when it's breached.</summary>
/// <param name="Name">Identifier of the constraint.</param>
/// <param name="Limit">The numeric limit this constraint enforces.</param>
/// <param name="Severity">How severe a breach of this constraint is.</param>
/// <param name="ActionOnBreach">What happens when this constraint is breached.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record RiskConstraint(string Name, double Limit, RiskSeverity Severity, RiskBreachAction ActionOnBreach);

/// <summary>
/// The recorded result of one workflow execution: what was expected against its objective versus what was
/// actually observed, plus cost/latency/risk-event detail. The durable input to evaluation and optimization —
/// neither can reason about "is this workflow doing well" without a history of these.
/// </summary>
/// <param name="ExecutionId">The execution this outcome describes.</param>
/// <param name="WorkflowId">The workflow this execution ran.</param>
/// <param name="Result">The execution's terminal status.</param>
/// <param name="RecordedAt">When this outcome was recorded.</param>
/// <param name="Expected">Expected metric values per the workflow's declared objective, if any.</param>
/// <param name="Observed">Actually observed metric values.</param>
/// <param name="Cost">Total cost incurred, if tracked.</param>
/// <param name="Latency">Total wall-clock duration, if tracked.</param>
/// <param name="RiskEvents">Identifiers of any <see cref="RiskConstraint"/> breaches that occurred.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record OutcomeRecord(
    string ExecutionId,
    string WorkflowId,
    WorkflowExecutionStatus Result,
    DateTimeOffset RecordedAt,
    IReadOnlyList<MetricObservation>? Expected = null,
    IReadOnlyList<MetricObservation>? Observed = null,
    decimal? Cost = null,
    TimeSpan? Latency = null,
    IReadOnlyList<string>? RiskEvents = null);

/// <summary>A point-in-time health summary for one workflow, derived from its recorded outcomes.</summary>
/// <param name="WorkflowId">The workflow this summary describes.</param>
/// <param name="EvaluatedAt">When this summary was computed.</param>
/// <param name="SampleSize">Number of outcomes this summary was computed from.</param>
/// <param name="SuccessRate">Fraction of sampled executions that reached <see cref="WorkflowExecutionStatus.Succeeded"/>, in [0, 1].</param>
/// <param name="AverageCost">Mean cost across sampled executions that reported one.</param>
/// <param name="AverageLatency">Mean latency across sampled executions that reported one.</param>
/// <param name="Concerns">Human-readable flags this summary raised (e.g. a declining success rate, a cost/latency regression).</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record WorkflowHealth(
    string WorkflowId,
    DateTimeOffset EvaluatedAt,
    int SampleSize,
    double SuccessRate,
    decimal? AverageCost = null,
    TimeSpan? AverageLatency = null,
    IReadOnlyList<string>? Concerns = null);

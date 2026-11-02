using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Agent.Contracts;

/// <summary>The last-known-good version of a workflow, with the metrics it established as its comparison bar.</summary>
/// <param name="WorkflowId">The workflow this baseline belongs to.</param>
/// <param name="ArtifactHash">Content hash of the active artifact this baseline was established from.</param>
/// <param name="BaselineDefinition">The baseline's compiled definition, so a candidate can be diffed against it for review-trigger properties (structural change, new tools, higher autonomy, newly-introduced mutating effects) rather than trusting a caller-supplied claim.</param>
/// <param name="BaselineMetrics">The metric values a candidate must not regress on.</param>
/// <param name="EstablishedAt">When this baseline was established.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record WorkflowBaseline(
    string WorkflowId,
    string ArtifactHash,
    WorkflowDefinition BaselineDefinition,
    IReadOnlyList<MetricObservation> BaselineMetrics,
    DateTimeOffset EstablishedAt);

/// <summary>
/// A reference to a set of historical executions a candidate should be replayed against before promotion.
/// This record identifies the dataset; the replay execution mechanism itself (actually re-running a
/// candidate artifact against each referenced execution) is not part of this contract.
/// </summary>
/// <param name="DatasetId">Identifier of this dataset.</param>
/// <param name="WorkflowId">The workflow this dataset replays.</param>
/// <param name="ExecutionIds">Identifiers of the historical executions included.</param>
/// <param name="CreatedAt">When this dataset was assembled.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record WorkflowReplayDataset(
    string DatasetId,
    string WorkflowId,
    IReadOnlyList<string> ExecutionIds,
    DateTimeOffset CreatedAt);

/// <summary>The result of replaying one candidate artifact against a <see cref="WorkflowReplayDataset"/>.</summary>
/// <param name="CandidateArtifactHash">The candidate artifact that was evaluated.</param>
/// <param name="DatasetId">The dataset it was replayed against.</param>
/// <param name="ObservedMetrics">Metric values observed during replay.</param>
/// <param name="PolicyRegressionDetected">Whether replay found the candidate violating a policy/risk constraint the baseline did not.</param>
/// <param name="WithinCostLatencyLimits">Whether replay stayed within the candidate's declared cost/latency budget.</param>
/// <param name="EvaluatedAt">When this evaluation was produced.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record WorkflowVariantEvaluation(
    string CandidateArtifactHash,
    string DatasetId,
    IReadOnlyList<MetricObservation> ObservedMetrics,
    bool PolicyRegressionDetected,
    bool WithinCostLatencyLimits,
    DateTimeOffset EvaluatedAt);

/// <summary>
/// A candidate improved version of a workflow, not yet active. Deliberately does NOT carry caller-asserted
/// "does this need review" flags — whether a candidate changes the step graph shape, introduces a new tool,
/// raises autonomy, or introduces a mutating effect is computed by diffing <see cref="Artifact"/>'s definition
/// against the baseline's (see <c>WorkflowCandidateDiff</c> in <c>Ampelum.Agent.Synthesis</c>), not trusted as
/// self-reported data on this record.
/// </summary>
/// <param name="WorkflowId">The workflow this candidate would replace.</param>
/// <param name="Artifact">The candidate's compiled artifact.</param>
/// <param name="GeneratedFromBaselineHash">The active artifact hash this candidate was generated from.</param>
/// <param name="GeneratedAt">When this candidate was generated.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record WorkflowOptimizationCandidate(
    string WorkflowId,
    WorkflowArtifact Artifact,
    string GeneratedFromBaselineHash,
    DateTimeOffset GeneratedAt);

/// <summary>Declared policy for canary-testing a promoted candidate before it becomes the sole active version.</summary>
/// <param name="TrafficPercentage">Fraction of traffic, in [0, 1], routed to the candidate during canary.</param>
/// <param name="MinimumObservationWindow">Minimum duration the candidate must run as a canary before full promotion.</param>
/// <param name="Stoppable">Whether the canary can be halted before its observation window elapses.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record CanaryPolicy(double TrafficPercentage, TimeSpan MinimumObservationWindow, bool Stoppable = true);

/// <summary>The result of evaluating a candidate for promotion.</summary>
/// <param name="Approved">Whether the candidate may be promoted right now.</param>
/// <param name="Reason">Why promotion was refused, if it was.</param>
/// <param name="RequiresHumanReview">Whether this candidate's properties mandate an explicit human review regardless of its metrics.</param>
/// <param name="ResultingState">The promotion state this decision resolves to.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record PromotionDecision(bool Approved, string? Reason, bool RequiresHumanReview, WorkflowPromotionState ResultingState);

/// <summary>The result of evaluating whether an active workflow version should be rolled back.</summary>
/// <param name="ShouldRollback">Whether a rollback should occur.</param>
/// <param name="RestoreArtifactHash">The exact prior active artifact hash to restore, if a rollback should occur.</param>
/// <param name="Reason">Why a rollback should (or should not) occur.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record RollbackDecision(bool ShouldRollback, string? RestoreArtifactHash, string? Reason);

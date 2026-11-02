using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Agent.Contracts;

/// <summary>Immutable input to a domain-owned workflow evaluation.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record EvaluationRequest(
    string EvaluationId,
    string ExecutionId,
    string EvaluatorId,
    DateTimeOffset RequestedAt,
    IReadOnlyDictionary<string, string> Evidence,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>Normalized result produced by an evaluator.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record EvaluationResult(
    string EvaluationId,
    bool Passed,
    decimal Score,
    string Summary,
    DateTimeOffset EvaluatedAt,
    IReadOnlyList<string>? Findings = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>Contract implemented by a domain-owned evaluator; Ampelum does not infer domain success.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IWorkflowEvaluator
{
    /// <summary>Evaluates immutable evidence for one workflow execution.</summary>
    ValueTask<EvaluationResult> EvaluateAsync(EvaluationRequest request, CancellationToken cancellationToken = default);
}

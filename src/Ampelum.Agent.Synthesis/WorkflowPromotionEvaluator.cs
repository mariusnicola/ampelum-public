using SignalGrid.Ampelum.Agent.Contracts;

namespace SignalGrid.Ampelum.Agent.Synthesis;

/// <summary>
/// Computes whether a candidate's properties mandate human review, by diffing its compiled
/// <see cref="WorkflowDefinition"/> against the baseline's, rather than trusting a caller-supplied flag.
/// </summary>
public static class WorkflowCandidateDiff
{
    /// <summary>Diffs <paramref name="candidate"/> against <paramref name="baseline"/> for the four review-trigger properties.</summary>
    public static (bool StructuralChange, bool IntroducesNewTools, bool IncreasesAutonomy, bool IntroducesMutatingEffect) Compute(
        WorkflowDefinition baseline, WorkflowDefinition candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        var baselineShape = baseline.Steps
            .Select(s => (s.StepId, s.Kind, DependsOn: string.Join(",", s.DependsOn.OrderBy(x => x, StringComparer.Ordinal)), Inputs: FormatInputs(s.Inputs)))
            .OrderBy(s => s.StepId, StringComparer.Ordinal)
            .ToArray();
        var candidateShape = candidate.Steps
            .Select(s => (s.StepId, s.Kind, DependsOn: string.Join(",", s.DependsOn.OrderBy(x => x, StringComparer.Ordinal)), Inputs: FormatInputs(s.Inputs)))
            .OrderBy(s => s.StepId, StringComparer.Ordinal)
            .ToArray();
        // Inputs is included here, not just (StepId, Kind, DependsOn) -- a FanOut step's workers are
        // encoded entirely as "worker.{i}.{key}" entries in Inputs (see WorkflowYamlCompiler), so a
        // candidate that adds/changes a worker without touching the step graph itself must still trip
        // structural-change review; ignoring Inputs here let worker changes bypass human review entirely.
        var structuralChange = !baselineShape.SequenceEqual(candidateShape);

        var baselineTools = new HashSet<string>(
            baseline.Steps.Where(s => s.Kind == WorkflowStepKind.Tool).Select(s => s.StepType), StringComparer.Ordinal);
        var introducesNewTools = candidate.Steps
            .Where(s => s.Kind == WorkflowStepKind.Tool)
            .Any(s => !baselineTools.Contains(s.StepType));

        var baselineMaxAutonomy = baseline.Steps.Select(s => (int?)s.MaxAutonomy).Max() ?? -1;
        var candidateMaxAutonomy = candidate.Steps.Select(s => (int?)s.MaxAutonomy).Max() ?? -1;
        var increasesAutonomy = candidateMaxAutonomy > baselineMaxAutonomy;

        var baselineHasMutatingEffect = baseline.Steps.Any(s => s.Effect is EffectClass.Mutating or EffectClass.Irreversible);
        var candidateHasMutatingEffect = candidate.Steps.Any(s => s.Effect is EffectClass.Mutating or EffectClass.Irreversible);
        var introducesMutatingEffect = candidateHasMutatingEffect && !baselineHasMutatingEffect;

        return (structuralChange, introducesNewTools, increasesAutonomy, introducesMutatingEffect);
    }

    /// <summary>Canonicalizes a step's <c>Inputs</c> for structural comparison -- sorted so key order never
    /// causes two semantically identical steps to register as changed.</summary>
    private static string FormatInputs(IReadOnlyDictionary<string, string>? inputs) =>
        inputs is null
            ? string.Empty
            : string.Join(";", inputs.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
}

/// <summary>
/// Deterministic, LLM-free promotion gate for a <see cref="WorkflowOptimizationCandidate"/>: evaluates the
/// spec's promotion conditions (candidate/baseline/evaluation identity match, no policy/risk regression,
/// cost/latency within limits, every declared objective metric actually compared and non-regressed, and
/// structural/new-tool/higher-autonomy/mutating-effect changes require human review) and produces one
/// <see cref="PromotionDecision"/>. Deliberately does not attempt to run the actual replay execution itself —
/// <see cref="WorkflowVariantEvaluation"/> is assumed already produced by whatever replayed the candidate
/// against a <see cref="WorkflowReplayDataset"/>; building that replay engine is separate, larger work this
/// evaluator does not attempt. This evaluator is also not yet wired into <c>DefaultWorkflowSynthesizer</c>'s
/// production write path (<c>PromoteAsync</c>) — that integration, plus the replay engine, are open follow-up
/// items, not silently assumed complete.
/// </summary>
public static class WorkflowPromotionEvaluator
{
    /// <summary>
    /// Evaluates whether <paramref name="candidate"/> may be promoted given <paramref name="baseline"/> and
    /// its <paramref name="evaluation"/>. Fails closed: an evaluation that doesn't identifiably belong to
    /// this candidate/baseline pair is refused outright, and a candidate that declares an objective metric
    /// with no matching baseline or observed value is refused rather than silently treated as "no
    /// regression" — the whole point of this gate is that missing evidence never reads as a pass. A
    /// candidate whose <see cref="WorkflowCandidateDiff"/> against the baseline finds a structural change, a
    /// new tool, higher autonomy, or a newly-introduced mutating effect is refused unless
    /// <paramref name="humanReviewApproved"/> is true — there is no path through this method that promotes
    /// such a candidate without it, and every returned <see cref="PromotionDecision"/> reports
    /// <c>RequiresHumanReview</c> accurately regardless of which check actually blocked it.
    /// </summary>
    public static PromotionDecision Evaluate(
        WorkflowOptimizationCandidate candidate,
        WorkflowBaseline baseline,
        WorkflowVariantEvaluation evaluation,
        bool humanReviewApproved = false)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(evaluation);

        if (!string.Equals(candidate.WorkflowId, baseline.WorkflowId, StringComparison.Ordinal))
            return new PromotionDecision(false, "Candidate and baseline belong to different workflows.", false, WorkflowPromotionState.Draft);
        if (!string.Equals(candidate.GeneratedFromBaselineHash, baseline.ArtifactHash, StringComparison.Ordinal))
            return new PromotionDecision(false, "Candidate was not generated from the supplied baseline's artifact.", false, WorkflowPromotionState.Draft);
        if (!string.Equals(evaluation.CandidateArtifactHash, candidate.Artifact.ArtifactHash, StringComparison.Ordinal))
            return new PromotionDecision(false, "Evaluation does not identifiably belong to this candidate's artifact.", false, WorkflowPromotionState.Draft);

        var (structuralChange, introducesNewTools, increasesAutonomy, introducesMutatingEffect) =
            WorkflowCandidateDiff.Compute(baseline.BaselineDefinition, candidate.Artifact.Definition);
        var requiresHumanReview = structuralChange || introducesNewTools || increasesAutonomy || introducesMutatingEffect;

        if (evaluation.PolicyRegressionDetected)
            return new PromotionDecision(false, "Candidate introduces a policy/risk regression versus baseline.", requiresHumanReview, WorkflowPromotionState.Draft);

        if (!evaluation.WithinCostLatencyLimits)
            return new PromotionDecision(false, "Candidate exceeds its declared cost/latency limits during replay.", requiresHumanReview, WorkflowPromotionState.Draft);

        if (candidate.Artifact.Definition.Objective is { } objective)
        {
            foreach (var metric in objective.Metrics)
            {
                var baselineValue = baseline.BaselineMetrics.FirstOrDefault(m => m.MetricId == metric.MetricId)?.Value;
                var observedValue = evaluation.ObservedMetrics.FirstOrDefault(m => m.MetricId == metric.MetricId)?.Value;
                if (baselineValue is null || observedValue is null)
                    return new PromotionDecision(
                        false,
                        $"Objective metric '{metric.MetricId}' has no baseline and/or observed value to compare -- missing evidence never counts as a pass.",
                        requiresHumanReview,
                        WorkflowPromotionState.Draft);

                var regressed = string.Equals(metric.Direction, "maximize", StringComparison.OrdinalIgnoreCase)
                    ? observedValue < baselineValue
                    : observedValue > baselineValue;
                if (regressed)
                    return new PromotionDecision(
                        false,
                        $"Candidate regresses objective metric '{metric.MetricId}' versus baseline ({observedValue} vs {baselineValue}, direction '{metric.Direction}').",
                        requiresHumanReview,
                        WorkflowPromotionState.Draft);
            }
        }

        if (requiresHumanReview && !humanReviewApproved)
            return new PromotionDecision(
                false,
                "Candidate requires human review (structural change, new tool, higher autonomy, or a newly-introduced mutating effect) before promotion.",
                true,
                WorkflowPromotionState.Proposed);

        return new PromotionDecision(true, null, requiresHumanReview, WorkflowPromotionState.Active);
    }

    /// <summary>
    /// Decides whether to roll back to <paramref name="previousActive"/>. Requires <paramref name="currentActive"/>
    /// so a rollback is verifiably relative to what is actually running, and requires
    /// <paramref name="previousActive"/> to be in <see cref="WorkflowPromotionState.Superseded"/> — the state a
    /// version reaches specifically by being replaced by <paramref name="currentActive"/> — rather than
    /// accepting any <see cref="WorkflowVersion"/> the caller hands in (a <c>Draft</c> or <c>Retired</c>
    /// version is never a valid rollback target). Only ever returns the exact prior active artifact hash —
    /// there is no code path that fabricates or regenerates a "close enough" replacement.
    /// </summary>
    public static RollbackDecision EvaluateRollback(WorkflowVersion currentActive, WorkflowVersion previousActive, string reason)
    {
        ArgumentNullException.ThrowIfNull(currentActive);
        ArgumentNullException.ThrowIfNull(previousActive);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (!string.Equals(currentActive.WorkflowId, previousActive.WorkflowId, StringComparison.Ordinal))
            return new RollbackDecision(false, null, "Current and previous versions belong to different workflows.");
        if (currentActive.State != WorkflowPromotionState.Active)
            return new RollbackDecision(false, null, $"'{currentActive.SemanticVersion}' is not the currently active version (state: {currentActive.State}).");
        if (previousActive.State != WorkflowPromotionState.Superseded)
            return new RollbackDecision(false, null, $"'{previousActive.SemanticVersion}' is not a superseded prior version (state: {previousActive.State}) -- refusing to roll back to an unverified target.");
        if (previousActive.ArtifactHash is null)
            return new RollbackDecision(false, null, "The prior version has no recorded artifact hash to restore.");

        return new RollbackDecision(true, previousActive.ArtifactHash, reason);
    }
}

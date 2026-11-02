using SignalGrid.Ampelum.Agent.Contracts;
using SignalGrid.Ampelum.Agent.Synthesis;
using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests.Synthesis;

public sealed class WorkflowPromotionEvaluatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void Evaluate_CandidateNotGeneratedFromSuppliedBaseline_Refused()
    {
        var baseline = MakeBaseline(MakeDefinition());
        var candidate = MakeCandidate(MakeArtifact(MakeDefinition()), generatedFromBaselineHash: "some-other-hash");
        var evaluation = MakeEvaluation(candidate);

        var decision = WorkflowPromotionEvaluator.Evaluate(candidate, baseline, evaluation);

        decision.Approved.ShouldBeFalse();
        decision.Reason.ShouldNotBeNull().ShouldContain("not generated from the supplied baseline");
    }

    [Fact]
    public void Evaluate_EvaluationDoesNotMatchCandidateArtifact_Refused()
    {
        var baselineDefinition = MakeDefinition();
        var baseline = MakeBaseline(baselineDefinition);
        var candidate = MakeCandidate(MakeArtifact(MakeDefinition(), hash: "candidate-hash"), generatedFromBaselineHash: baseline.ArtifactHash);
        var evaluation = new WorkflowVariantEvaluation("mismatched-hash", "ds-1", [], false, true, Now);

        var decision = WorkflowPromotionEvaluator.Evaluate(candidate, baseline, evaluation);

        decision.Approved.ShouldBeFalse();
        decision.Reason.ShouldNotBeNull().ShouldContain("does not identifiably belong");
    }

    [Fact]
    public void Evaluate_PolicyRegressionDetected_Refused()
    {
        var baselineDefinition = MakeDefinition();
        var baseline = MakeBaseline(baselineDefinition);
        var candidate = MakeCandidate(MakeArtifact(MakeDefinition()), baseline.ArtifactHash);
        var evaluation = MakeEvaluation(candidate, policyRegression: true);

        var decision = WorkflowPromotionEvaluator.Evaluate(candidate, baseline, evaluation);

        decision.Approved.ShouldBeFalse();
        decision.RequiresHumanReview.ShouldBeFalse();
        decision.ResultingState.ShouldBe(WorkflowPromotionState.Draft);
    }

    [Fact]
    public void Evaluate_ExceedsCostLatencyLimits_Refused()
    {
        var baseline = MakeBaseline(MakeDefinition());
        var candidate = MakeCandidate(MakeArtifact(MakeDefinition()), baseline.ArtifactHash);
        var evaluation = MakeEvaluation(candidate, withinLimits: false);

        var decision = WorkflowPromotionEvaluator.Evaluate(candidate, baseline, evaluation);

        decision.Approved.ShouldBeFalse();
    }

    [Fact]
    public void Evaluate_ObjectiveMetricDeclaredButNoDataToCompare_RefusedRatherThanTreatedAsPass()
    {
        // Regression test: missing evidence must never silently read as "no regression detected."
        var definition = MakeDefinition(new WorkflowObjective([new WorkflowObjectiveMetric("outcome_quality", "maximize")]));
        var baseline = MakeBaseline(definition, metrics: []); // no baseline value recorded for outcome_quality
        var candidate = MakeCandidate(MakeArtifact(definition), baseline.ArtifactHash);
        var evaluation = MakeEvaluation(candidate, observed: []); // no observed value either

        var decision = WorkflowPromotionEvaluator.Evaluate(candidate, baseline, evaluation);

        decision.Approved.ShouldBeFalse();
        decision.Reason.ShouldNotBeNull().ShouldContain("no baseline and/or observed value");
    }

    [Fact]
    public void Evaluate_ObjectiveMetricRegresses_Refused()
    {
        var definition = MakeDefinition(new WorkflowObjective([new WorkflowObjectiveMetric("outcome_quality", "maximize")]));
        var baseline = MakeBaseline(definition, metrics: [new MetricObservation("outcome_quality", 0.9, Now, "eval")]);
        var candidate = MakeCandidate(MakeArtifact(definition), baseline.ArtifactHash);
        var evaluation = MakeEvaluation(candidate, observed: [new MetricObservation("outcome_quality", 0.7, Now, "eval")]);

        var decision = WorkflowPromotionEvaluator.Evaluate(candidate, baseline, evaluation);

        decision.Approved.ShouldBeFalse();
        decision.Reason.ShouldNotBeNull().ShouldContain("regresses objective metric");
    }

    [Fact]
    public void Evaluate_ObjectiveMetricImproves_NotBlockedOnThatGround()
    {
        var definition = MakeDefinition(new WorkflowObjective([new WorkflowObjectiveMetric("outcome_quality", "maximize")]));
        var baseline = MakeBaseline(definition, metrics: [new MetricObservation("outcome_quality", 0.7, Now, "eval")]);
        var candidate = MakeCandidate(MakeArtifact(definition), baseline.ArtifactHash);
        var evaluation = MakeEvaluation(candidate, observed: [new MetricObservation("outcome_quality", 0.9, Now, "eval")]);

        var decision = WorkflowPromotionEvaluator.Evaluate(candidate, baseline, evaluation);

        decision.Approved.ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_MinimizeDirectionMetricRegresses_Refused()
    {
        var definition = MakeDefinition(new WorkflowObjective([new WorkflowObjectiveMetric("execution_cost", "minimize")]));
        var baseline = MakeBaseline(definition, metrics: [new MetricObservation("execution_cost", 10, Now, "eval")]);
        var candidate = MakeCandidate(MakeArtifact(definition), baseline.ArtifactHash);
        var evaluation = MakeEvaluation(candidate, observed: [new MetricObservation("execution_cost", 15, Now, "eval")]);

        var decision = WorkflowPromotionEvaluator.Evaluate(candidate, baseline, evaluation);

        decision.Approved.ShouldBeFalse();
    }

    [Fact]
    public void Evaluate_StructuralChangeWithoutHumanReview_RequiresReviewAndRefuses()
    {
        var baselineDefinition = MakeDefinition(); // one step
        var baseline = MakeBaseline(baselineDefinition);
        var candidateDefinition = new WorkflowDefinition("wf", "v2", "Test",
            [new WorkflowStepDefinition("step-1", "test", []), new WorkflowStepDefinition("step-2", "test", ["step-1"])]); // two steps: structural change
        var candidate = MakeCandidate(MakeArtifact(candidateDefinition), baseline.ArtifactHash);
        var evaluation = MakeEvaluation(candidate);

        var decision = WorkflowPromotionEvaluator.Evaluate(candidate, baseline, evaluation);

        decision.Approved.ShouldBeFalse();
        decision.RequiresHumanReview.ShouldBeTrue();
        decision.ResultingState.ShouldBe(WorkflowPromotionState.Proposed);
    }

    [Fact]
    public void Evaluate_StructuralChangeWithHumanReviewApproved_Approved()
    {
        var baselineDefinition = MakeDefinition();
        var baseline = MakeBaseline(baselineDefinition);
        var candidateDefinition = new WorkflowDefinition("wf", "v2", "Test",
            [new WorkflowStepDefinition("step-1", "test", []), new WorkflowStepDefinition("step-2", "test", ["step-1"])]);
        var candidate = MakeCandidate(MakeArtifact(candidateDefinition), baseline.ArtifactHash);
        var evaluation = MakeEvaluation(candidate);

        var decision = WorkflowPromotionEvaluator.Evaluate(candidate, baseline, evaluation, humanReviewApproved: true);

        decision.Approved.ShouldBeTrue();
        decision.RequiresHumanReview.ShouldBeTrue();
        decision.ResultingState.ShouldBe(WorkflowPromotionState.Active);
    }

    [Fact]
    public void Evaluate_CandidateIntroducesNewTool_RequiresHumanReview()
    {
        var baselineDefinition = new WorkflowDefinition("wf", "v1", "Test", [new WorkflowStepDefinition("step-1", "read_file", [], Kind: WorkflowStepKind.Tool)]);
        var baseline = MakeBaseline(baselineDefinition);
        var candidateDefinition = new WorkflowDefinition("wf", "v2", "Test", [new WorkflowStepDefinition("step-1", "run_shell", [], Kind: WorkflowStepKind.Tool)]);
        var candidate = MakeCandidate(MakeArtifact(candidateDefinition), baseline.ArtifactHash);
        var evaluation = MakeEvaluation(candidate);

        var decision = WorkflowPromotionEvaluator.Evaluate(candidate, baseline, evaluation);

        decision.RequiresHumanReview.ShouldBeTrue();
        decision.Approved.ShouldBeFalse();
    }

    [Fact]
    public void Evaluate_CandidateIncreasesAutonomy_RequiresHumanReview()
    {
        var baselineDefinition = new WorkflowDefinition("wf", "v1", "Test", [new WorkflowStepDefinition("step-1", "t", [], Kind: WorkflowStepKind.Tool, MaxAutonomy: AutonomyMode.RequireApproval)]);
        var baseline = MakeBaseline(baselineDefinition);
        var candidateDefinition = new WorkflowDefinition("wf", "v2", "Test", [new WorkflowStepDefinition("step-1", "t", [], Kind: WorkflowStepKind.Tool, MaxAutonomy: AutonomyMode.AutomaticWithinPolicy)]);
        var candidate = MakeCandidate(MakeArtifact(candidateDefinition), baseline.ArtifactHash);
        var evaluation = MakeEvaluation(candidate);

        var decision = WorkflowPromotionEvaluator.Evaluate(candidate, baseline, evaluation);

        decision.RequiresHumanReview.ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_CandidateIntroducesMutatingEffect_RequiresHumanReview()
    {
        var baselineDefinition = new WorkflowDefinition("wf", "v1", "Test", [new WorkflowStepDefinition("step-1", "t", [], Kind: WorkflowStepKind.Tool, Effect: EffectClass.ReadOnly)]);
        var baseline = MakeBaseline(baselineDefinition);
        var candidateDefinition = new WorkflowDefinition("wf", "v2", "Test", [new WorkflowStepDefinition("step-1", "t", [], Kind: WorkflowStepKind.Tool, Effect: EffectClass.Mutating, CompensationStepId: "undo")]);
        var candidate = MakeCandidate(MakeArtifact(candidateDefinition), baseline.ArtifactHash);
        var evaluation = MakeEvaluation(candidate);

        var decision = WorkflowPromotionEvaluator.Evaluate(candidate, baseline, evaluation);

        decision.RequiresHumanReview.ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_CandidateChangesFanOutWorkers_RequiresHumanReview()
    {
        // Regression test: FanOut workers are encoded as "worker.{i}.{key}" entries in Inputs, not as
        // separate graph steps or a Kind==Tool StepType -- prior to including Inputs in the structural
        // diff, adding a worker to a FanOut step tripped none of the four review triggers and could
        // silently auto-promote.
        var baselineDefinition = new WorkflowDefinition("wf", "v1", "Test",
        [
            new WorkflowStepDefinition("fan", "fan", [], Kind: WorkflowStepKind.FanOut,
                Inputs: new Dictionary<string, string> { ["worker.0.tool"] = "market-research" }),
        ]);
        var baseline = MakeBaseline(baselineDefinition);
        var candidateDefinition = new WorkflowDefinition("wf", "v2", "Test",
        [
            new WorkflowStepDefinition("fan", "fan", [], Kind: WorkflowStepKind.FanOut,
                Inputs: new Dictionary<string, string>
                {
                    ["worker.0.tool"] = "market-research",
                    ["worker.1.tool"] = "run_shell_thing",
                }),
        ]);
        var candidate = MakeCandidate(MakeArtifact(candidateDefinition), baseline.ArtifactHash);
        var evaluation = MakeEvaluation(candidate);

        var decision = WorkflowPromotionEvaluator.Evaluate(candidate, baseline, evaluation);

        decision.RequiresHumanReview.ShouldBeTrue();
        decision.Approved.ShouldBeFalse();
    }

    [Fact]
    public void Evaluate_NoTriggersAndPasses_ApprovedWithoutHumanReview()
    {
        var baseline = MakeBaseline(MakeDefinition());
        var candidate = MakeCandidate(MakeArtifact(MakeDefinition()), baseline.ArtifactHash);
        var evaluation = MakeEvaluation(candidate);

        var decision = WorkflowPromotionEvaluator.Evaluate(candidate, baseline, evaluation);

        decision.Approved.ShouldBeTrue();
        decision.RequiresHumanReview.ShouldBeFalse();
        decision.ResultingState.ShouldBe(WorkflowPromotionState.Active);
    }

    [Fact]
    public void EvaluateRollback_ValidActiveAndSupersededPair_RestoresExactHash()
    {
        var current = new WorkflowVersion("wf", "2.0.0", WorkflowPromotionState.Active, Now);
        var previous = new WorkflowVersion("wf", "1.0.0", WorkflowPromotionState.Superseded, Now, ArtifactHash: "hash-abc");

        var decision = WorkflowPromotionEvaluator.EvaluateRollback(current, previous, "regression detected");

        decision.ShouldRollback.ShouldBeTrue();
        decision.RestoreArtifactHash.ShouldBe("hash-abc");
    }

    [Fact]
    public void EvaluateRollback_PreviousVersionNotSuperseded_RefusesRatherThanRollingBackToUnverifiedTarget()
    {
        var current = new WorkflowVersion("wf", "2.0.0", WorkflowPromotionState.Active, Now);
        var draft = new WorkflowVersion("wf", "0.9.0", WorkflowPromotionState.Draft, Now, ArtifactHash: "hash-draft");

        var decision = WorkflowPromotionEvaluator.EvaluateRollback(current, draft, "regression detected");

        decision.ShouldRollback.ShouldBeFalse();
    }

    [Fact]
    public void EvaluateRollback_CurrentVersionNotActive_Refuses()
    {
        var notActive = new WorkflowVersion("wf", "2.0.0", WorkflowPromotionState.Draft, Now);
        var previous = new WorkflowVersion("wf", "1.0.0", WorkflowPromotionState.Superseded, Now, ArtifactHash: "hash-abc");

        var decision = WorkflowPromotionEvaluator.EvaluateRollback(notActive, previous, "regression detected");

        decision.ShouldRollback.ShouldBeFalse();
    }

    [Fact]
    public void EvaluateRollback_PriorVersionHasNoArtifactHash_RefusesRatherThanFabricating()
    {
        var current = new WorkflowVersion("wf", "2.0.0", WorkflowPromotionState.Active, Now);
        var previous = new WorkflowVersion("wf", "1.0.0", WorkflowPromotionState.Superseded, Now, ArtifactHash: null);

        var decision = WorkflowPromotionEvaluator.EvaluateRollback(current, previous, "regression detected");

        decision.ShouldRollback.ShouldBeFalse();
        decision.RestoreArtifactHash.ShouldBeNull();
    }

    private static WorkflowDefinition MakeDefinition(WorkflowObjective? objective = null) =>
        new("wf", "v1", "Test", [new WorkflowStepDefinition("step-1", "test", [])], Objective: objective);

    private static WorkflowArtifact MakeArtifact(WorkflowDefinition definition, string hash = "candidate-hash") =>
        new(hash, "schema-hash", "policy-hash", definition);

    private static WorkflowBaseline MakeBaseline(WorkflowDefinition definition, IReadOnlyList<MetricObservation>? metrics = null) =>
        new("wf", "baseline-hash", definition, metrics ?? [], Now);

    private static WorkflowOptimizationCandidate MakeCandidate(WorkflowArtifact artifact, string generatedFromBaselineHash) =>
        new("wf", artifact, generatedFromBaselineHash, Now);

    private static WorkflowVariantEvaluation MakeEvaluation(
        WorkflowOptimizationCandidate candidate,
        bool policyRegression = false,
        bool withinLimits = true,
        IReadOnlyList<MetricObservation>? observed = null) =>
        new(candidate.Artifact.ArtifactHash, "ds-1", observed ?? [], policyRegression, withinLimits, Now);
}

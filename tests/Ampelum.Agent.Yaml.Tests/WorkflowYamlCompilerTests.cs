using SignalGrid.Ampelum.Agent.Contracts;
using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Yaml.Tests;

public sealed class WorkflowYamlCompilerTests
{
    [Fact]
    public void Compile_ValidMinimalWorkflow_Succeeds()
    {
        const string yaml = """
            id: minimal
            version: 1.0.0
            steps:
              - id: step-1
                kind: tool
            """;

        var result = WorkflowYamlCompiler.Compile(yaml);

        result.Success.ShouldBeTrue(string.Join("; ", result.Errors));
        result.Artifact.ShouldNotBeNull();
        result.Artifact!.Definition.WorkflowId.ShouldBe("minimal");
        result.Artifact.Definition.Steps.Count.ShouldBe(1);
        result.Artifact.ArtifactHash.ShouldNotBeNullOrWhiteSpace();
        result.Artifact.SchemaHash.ShouldNotBeNullOrWhiteSpace();
        result.Artifact.PolicyHash.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Compile_MissingId_Fails()
    {
        const string yaml = """
            version: 1.0.0
            steps:
              - id: step-1
            """;

        var result = WorkflowYamlCompiler.Compile(yaml);

        result.Success.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("'id'"));
    }

    [Fact]
    public void Compile_NoSteps_Fails()
    {
        const string yaml = """
            id: no-steps
            version: 1.0.0
            """;

        var result = WorkflowYamlCompiler.Compile(yaml);

        result.Success.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("at least one step"));
    }

    [Fact]
    public void Compile_DuplicateStepIds_Fails()
    {
        const string yaml = """
            id: dup
            version: 1.0.0
            steps:
              - id: step-1
              - id: step-1
            """;

        var result = WorkflowYamlCompiler.Compile(yaml);

        result.Success.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("Duplicate step id"));
    }

    [Fact]
    public void Compile_DanglingDependency_Fails()
    {
        const string yaml = """
            id: dangling
            version: 1.0.0
            steps:
              - id: step-1
                depends_on: [nonexistent]
            """;

        var result = WorkflowYamlCompiler.Compile(yaml);

        result.Success.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("unknown step 'nonexistent'"));
    }

    [Fact]
    public void Compile_UnknownStepKind_Fails()
    {
        const string yaml = """
            id: unknown-kind
            version: 1.0.0
            steps:
              - id: step-1
                kind: teleport
            """;

        var result = WorkflowYamlCompiler.Compile(yaml);

        result.Success.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("unknown kind"));
    }

    [Fact]
    public void Compile_UnknownSchemaVersion_Fails()
    {
        const string yaml = """
            schema_version: "99.0"
            id: future
            version: 1.0.0
            steps:
              - id: step-1
            """;

        var result = WorkflowYamlCompiler.Compile(yaml);

        result.Success.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("Unknown schema version"));
    }

    [Fact]
    public void Compile_BranchWithoutExplicitJoin_Fails()
    {
        const string yaml = """
            id: branch-no-join
            version: 1.0.0
            steps:
              - id: branch
                kind: branch
                branch_targets: [a, b]
              - id: a
                depends_on: [branch]
              - id: b
                depends_on: [branch]
            """;

        var result = WorkflowYamlCompiler.Compile(yaml);

        result.Success.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("no explicit join"));
    }

    [Fact]
    public void Compile_BranchWithExplicitJoin_Succeeds()
    {
        const string yaml = """
            id: branch-with-join
            version: 1.0.0
            steps:
              - id: branch
                kind: branch
                branch_targets: [a, b]
              - id: a
                depends_on: [branch]
              - id: b
                depends_on: [branch]
              - id: merge
                kind: join
                depends_on: [a, b]
            """;

        var result = WorkflowYamlCompiler.Compile(yaml);

        result.Success.ShouldBeTrue(string.Join("; ", result.Errors));
    }

    [Fact]
    public void Compile_LoopWithoutMaxIterations_Fails()
    {
        const string yaml = """
            id: loop-no-bound
            version: 1.0.0
            steps:
              - id: step-1
                kind: loop
            """;

        var result = WorkflowYamlCompiler.Compile(yaml);

        result.Success.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("positive max_iterations"));
    }

    [Fact]
    public void Compile_FanOutWithoutWorkers_Fails()
    {
        const string yaml = """
            id: fanout-no-workers
            version: 1.0.0
            steps:
              - id: research
                kind: fan_out
              - id: merge
                kind: join
                depends_on: [research]
            """;

        var result = WorkflowYamlCompiler.Compile(yaml);

        result.Success.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("must declare at least one worker"));
    }

    [Fact]
    public void Compile_FanOutWithoutJoin_Fails()
    {
        const string yaml = """
            id: fanout-no-join
            version: 1.0.0
            steps:
              - id: research
                kind: fan_out
                workers:
                  - agent: market-research
            """;

        var result = WorkflowYamlCompiler.Compile(yaml);

        result.Success.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("has no join step"));
    }

    [Fact]
    public void Compile_MutatingEffectWithoutCompensationOrJustification_Fails()
    {
        const string yaml = """
            id: mutating-no-comp
            version: 1.0.0
            steps:
              - id: step-1
                kind: tool
                effect: mutating
            """;

        var result = WorkflowYamlCompiler.Compile(yaml);

        result.Success.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("neither a compensation step nor an explicit justification"));
    }

    [Fact]
    public void Compile_MutatingEffectWithJustification_Succeeds()
    {
        const string yaml = """
            id: mutating-justified
            version: 1.0.0
            steps:
              - id: step-1
                kind: tool
                effect: mutating
                effect_justification: "No compensation available; effect is intentionally permanent."
            """;

        var result = WorkflowYamlCompiler.Compile(yaml);

        result.Success.ShouldBeTrue(string.Join("; ", result.Errors));
        result.Artifact!.Definition.Steps[0].Effect.ShouldBe(EffectClass.Mutating);
    }

    [Fact]
    public void Compile_MutatingEffectWithCompensationStep_Succeeds()
    {
        const string yaml = """
            id: mutating-compensated
            version: 1.0.0
            steps:
              - id: step-1
                kind: tool
                effect: mutating
                compensation_step: undo-step-1
              - id: undo-step-1
                kind: compensate
            """;

        var result = WorkflowYamlCompiler.Compile(yaml);

        // The YAML compiler's job is static schema validation, not execution-readiness -- confirming
        // the compensation-declared step compiles cleanly overall, not merely that one specific error is absent.
        result.Success.ShouldBeTrue(string.Join("; ", result.Errors));
        result.Errors.ShouldNotContain(e => e.Contains("neither a compensation step nor an explicit justification"));
    }

    [Fact]
    public void Compile_WaitForSignalEvaluateEmitEventAndCompensateKinds_AreMappedWithTheirInputs()
    {
        const string yaml = """
            id: all-kinds
            version: 1.0.0
            steps:
              - id: charge
                kind: tool
                effect: mutating
                compensation_step: refund
              - id: receipt
                kind: wait_for_signal
                depends_on: [charge]
                inputs:
                  signal: receipt-arrived
              - id: check
                kind: evaluate
                depends_on: [receipt]
              - id: announce
                kind: emit_event
                depends_on: [check]
                inputs:
                  event: expense.settled
                  detail: done
              - id: refund
                kind: compensate
            """;

        var result = WorkflowYamlCompiler.Compile(yaml);

        result.Success.ShouldBeTrue(string.Join("; ", result.Errors));
        var steps = result.Artifact!.Definition.Steps.ToDictionary(step => step.StepId);
        steps["receipt"].Kind.ShouldBe(WorkflowStepKind.WaitForSignal);
        steps["receipt"].Inputs!["signal"].ShouldBe("receipt-arrived");
        steps["check"].Kind.ShouldBe(WorkflowStepKind.Evaluate);
        steps["announce"].Kind.ShouldBe(WorkflowStepKind.EmitEvent);
        steps["announce"].Inputs!["event"].ShouldBe("expense.settled");
        steps["announce"].Inputs!["detail"].ShouldBe("done");
        steps["refund"].Kind.ShouldBe(WorkflowStepKind.Compensate);
        steps["charge"].CompensationStepId.ShouldBe("refund");
    }

    [Fact]
    public void Compile_ToolStepDeclaresToolField_ResolvesStepTypeToTheToolName()
    {
        const string yaml = """
            id: with-tool
            version: 1.0.0
            steps:
              - id: read-step
                kind: tool
                tool: read_file
            """;

        var result = WorkflowYamlCompiler.Compile(yaml);

        result.Success.ShouldBeTrue(string.Join("; ", result.Errors));
        result.Artifact!.Definition.Steps.Single().StepType.ShouldBe("read_file");
    }

    [Fact]
    public void Compile_ToolStepWithoutToolField_ResolvesStepTypeToStepId()
    {
        const string yaml = """
            id: without-tool
            version: 1.0.0
            steps:
              - id: read-step
                kind: tool
            """;

        var result = WorkflowYamlCompiler.Compile(yaml);

        result.Success.ShouldBeTrue(string.Join("; ", result.Errors));
        result.Artifact!.Definition.Steps.Single().StepType.ShouldBe("read-step");
    }

    [Fact]
    public void Compile_ToolNotOnItsOwnAllowList_Fails()
    {
        const string yaml = """
            id: inconsistent-allowlist
            version: 1.0.0
            steps:
              - id: read-step
                kind: tool
                tool: read_file
                tool_allow_list: [write_file]
            """;

        var result = WorkflowYamlCompiler.Compile(yaml);

        result.Success.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("not on its own declared tool_allow_list"));
    }

    [Fact]
    public void Compile_NegativeBudget_Fails()
    {
        const string yaml = """
            id: negative-budget
            version: 1.0.0
            risk:
              max_effect_class: mutating
              max_cost: -5
            steps:
              - id: step-1
            """;

        var result = WorkflowYamlCompiler.Compile(yaml);

        result.Success.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("negative max_cost"));
    }

    [Fact]
    public void Compile_SameYamlTwice_ProducesIdenticalArtifactHash()
    {
        const string yaml = """
            id: deterministic
            version: 1.0.0
            steps:
              - id: step-1
                kind: tool
            """;

        var first = WorkflowYamlCompiler.Compile(yaml);
        var second = WorkflowYamlCompiler.Compile(yaml);

        first.Artifact!.ArtifactHash.ShouldBe(second.Artifact!.ArtifactHash);
        first.Artifact.SchemaHash.ShouldBe(second.Artifact.SchemaHash);
        first.Artifact.PolicyHash.ShouldBe(second.Artifact.PolicyHash);
    }

    [Fact]
    public void Compile_DescriptionField_ToleratedAndFoldedIntoMetadata()
    {
        const string yaml = """
            id: with-description
            version: 1.0.0
            description: A workflow that does a thing.
            steps:
              - id: step-1
            """;

        var result = WorkflowYamlCompiler.Compile(yaml);

        result.Success.ShouldBeTrue(string.Join("; ", result.Errors));
        result.Artifact!.Definition.Metadata!["description"].ShouldBe("A workflow that does a thing.");
    }

    [Fact]
    public void Compile_UnrecognizedTopLevelKey_FailsRatherThanSilentlyDropping()
    {
        const string yaml = """
            id: typo-test
            versoin: 1.0.0
            steps:
              - id: step-1
            """;

        var result = WorkflowYamlCompiler.Compile(yaml);

        result.Success.ShouldBeFalse();
    }

    [Fact]
    public void Compile_UnrecognizedStepLevelKey_FailsRatherThanSilentlyDropping()
    {
        const string yaml = """
            id: typo-test-2
            version: 1.0.0
            steps:
              - id: step-1
                dependes_on: [nonexistent]
            """;

        var result = WorkflowYamlCompiler.Compile(yaml);

        result.Success.ShouldBeFalse();
    }

    [Fact]
    public void Compile_WorkersExpansionCollidesWithExplicitInputsKey_Fails()
    {
        const string yaml = """
            id: worker-collision
            version: 1.0.0
            steps:
              - id: research
                kind: fan_out
                inputs:
                  worker.0.agent: explicit-value
                workers:
                  - agent: worker-value
              - id: merge
                kind: join
                depends_on: [research]
            """;

        var result = WorkflowYamlCompiler.Compile(yaml);

        result.Success.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("collides with its 'workers' expansion"));
    }

    [Fact]
    public void Compile_NegativeStepTimeout_Fails()
    {
        const string yaml = """
            id: negative-timeout
            version: 1.0.0
            steps:
              - id: step-1
                timeout_seconds: -1
            """;

        var result = WorkflowYamlCompiler.Compile(yaml);

        result.Success.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("negative timeout_seconds"));
    }

    [Fact]
    public void Compile_SemanticallyEquivalentDocumentsWithDifferentKeyOrder_ProduceIdenticalArtifactHash()
    {
        const string first = """
            id: order-independent
            version: 1.0.0
            steps:
              - id: research
                kind: fan_out
                workers:
                  - agent: market-research
                    region: us
              - id: merge
                kind: join
                depends_on: [research]
            """;
        const string second = """
            id: order-independent
            version: 1.0.0
            steps:
              - id: research
                kind: fan_out
                workers:
                  - region: us
                    agent: market-research
              - id: merge
                kind: join
                depends_on: [research]
            """;

        var firstResult = WorkflowYamlCompiler.Compile(first);
        var secondResult = WorkflowYamlCompiler.Compile(second);

        firstResult.Success.ShouldBeTrue(string.Join("; ", firstResult.Errors));
        secondResult.Success.ShouldBeTrue(string.Join("; ", secondResult.Errors));
        firstResult.Artifact!.ArtifactHash.ShouldBe(secondResult.Artifact!.ArtifactHash);
    }

    [Fact]
    public void Compile_MalformedYaml_FailsGracefullyRatherThanThrowing()
    {
        const string yaml = "id: [this is not valid: yaml: at all}}}";

        var result = WorkflowYamlCompiler.Compile(yaml);

        result.Success.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("did not parse"));
    }

    const string EvaluateWorkflow = """
        id: scored
        version: 1.0.0
        objective:
          metrics:
            - id: quality
              direction: maximize
              threshold: 0.8
            - id: cost
              direction: minimize
        {0}
        steps:
          - id: judge
            kind: evaluate
        """;

    [Fact]
    public void Compile_MetricThreshold_ReachesTheObjective()
    {
        var result = WorkflowYamlCompiler.Compile(string.Format(EvaluateWorkflow, ""));

        result.Success.ShouldBeTrue(string.Join("; ", result.Errors));
        var metrics = result.Artifact!.Definition.Objective!.Metrics;
        metrics.Single(m => m.MetricId == "quality").Threshold.ShouldBe(0.8);
        metrics.Single(m => m.MetricId == "cost").Threshold.ShouldBeNull();
    }

    [Fact]
    public void Compile_EvaluationSection_BecomesTheEvaluationPlan()
    {
        var result = WorkflowYamlCompiler.Compile(string.Format(EvaluateWorkflow, "evaluation:\n  metrics: [quality, cost]"));

        result.Success.ShouldBeTrue(string.Join("; ", result.Errors));
        var plan = result.Artifact!.Definition.EvaluationPlan.ShouldNotBeNull();
        plan.MetricIds.ShouldBe(["quality", "cost"]);
        plan.Enabled.ShouldBeTrue();
    }

    [Fact]
    public void Compile_EvaluationSection_CanBeSwitchedOff()
    {
        var result = WorkflowYamlCompiler.Compile(string.Format(EvaluateWorkflow, "evaluation:\n  enabled: false\n  metrics: [quality]"));

        result.Success.ShouldBeTrue(string.Join("; ", result.Errors));
        result.Artifact!.Definition.EvaluationPlan.ShouldNotBeNull().Enabled.ShouldBeFalse();
    }

    [Fact]
    public void Compile_WithoutEvaluationSection_LeavesNoPlan()
    {
        var result = WorkflowYamlCompiler.Compile(string.Format(EvaluateWorkflow, ""));

        result.Artifact!.Definition.EvaluationPlan.ShouldBeNull();
    }

    [Theory]
    [InlineData("evaluation:\n  metrics: [quality, speed]", "'speed'")]
    [InlineData("evaluation:\n  metrics: []", "at least one metric")]
    [InlineData("evaluation:\n  enabled: true", "at least one metric")]
    [InlineData("evaluation:\n  metrics: [quality, quality]", "'quality' more than once")]
    [InlineData("evaluation:\n  metrics: [\"\"]", "blank")]
    public void Compile_BadEvaluationSection_IsRejectedWithAReason(string section, string expected)
    {
        var result = WorkflowYamlCompiler.Compile(string.Format(EvaluateWorkflow, section));

        result.Success.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains(expected));
    }

    [Fact]
    public void Compile_EvaluationSectionWithoutObjectiveMetrics_IsRejected()
    {
        var result = WorkflowYamlCompiler.Compile("""
            id: scored
            version: 1.0.0
            evaluation:
              metrics: [quality]
            steps:
              - id: judge
                kind: evaluate
            """);

        result.Success.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("'quality'"));
    }

    [Theory]
    [InlineData(".nan")]
    [InlineData(".inf")]
    public void Compile_NonFiniteThreshold_IsRejected(string value)
    {
        var yaml = string.Format(EvaluateWorkflow, "").Replace("threshold: 0.8", "threshold: " + value);

        var result = WorkflowYamlCompiler.Compile(yaml);

        result.Success.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("'quality'") && e.Contains("threshold"));
    }

    [Fact]
    public void Compile_FullSpecExampleYaml_Succeeds()
    {
        const string yaml = """
            id: opportunity-research
            version: 1.0.0
            objective:
              metrics:
                - id: outcome_quality
                  direction: maximize
                - id: execution_cost
                  direction: minimize
            risk:
              max_effect_class: mutating
              max_cost: 25
              require_approval_for: [mutating, irreversible]
            context:
              required:
                - evidence: market_snapshot
                - knowledge: relevant_playbooks
            triggers:
              - type: manual
            steps:
              - id: research
                kind: fan_out
                workers:
                  - agent: market-research
                  - agent: internal-evidence
              - id: merge
                kind: join
                depends_on: [research]
              - id: evaluate
                kind: evaluate
                depends_on: [merge]
              - id: act
                kind: tool
                depends_on: [evaluate]
                autonomy: require_approval
                effect: mutating
                effect_justification: "Acting on validated research; no automated compensation modeled yet."
            optimization:
              enabled: true
              promotion: canary
            """;

        var result = WorkflowYamlCompiler.Compile(yaml);

        result.Success.ShouldBeTrue(string.Join("; ", result.Errors));
        var definition = result.Artifact!.Definition;
        definition.WorkflowId.ShouldBe("opportunity-research");
        definition.Objective!.Metrics.Count.ShouldBe(2);
        definition.Policy!.MaxEffectClass.ShouldBe(EffectClass.Mutating);
        definition.Policy.RequireApprovalFor.ShouldBe([EffectClass.Mutating, EffectClass.Irreversible]);
        definition.ContextRequests!.Count.ShouldBe(2);
        definition.Triggers!.Single().Type.ShouldBe("manual");
        definition.Optimization!.Enabled.ShouldBeTrue();
        definition.Optimization.Promotion.ShouldBe("canary");
        definition.Steps.Single(s => s.StepId == "research").Kind.ShouldBe(WorkflowStepKind.FanOut);
    }
}

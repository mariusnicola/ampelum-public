using SignalGrid.Ampelum.Agent.Contracts;
using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

public sealed class WorkflowPolicyGateTests
{
    private readonly WorkflowPolicyGate _gate = new();

    [Fact]
    public void Evaluate_StepDeclaresForbiddenAutonomy_AlwaysBlocked()
    {
        var step = new WorkflowStepDefinition("step-1", "test", [], Kind: WorkflowStepKind.Tool, MaxAutonomy: AutonomyMode.Forbidden);
        var workflow = new WorkflowDefinition("wf", "v1", "Test", [step]);

        var decision = _gate.Evaluate(workflow, step);

        decision.Decision.ShouldBe(Decision.Block);
    }

    [Fact]
    public void Evaluate_ToolNotOnStepAllowList_Blocked()
    {
        var step = new WorkflowStepDefinition("step-1", "test", [], Kind: WorkflowStepKind.Tool, ToolAllowList: ["read_file"]);
        var workflow = new WorkflowDefinition("wf", "v1", "Test", [step]);

        var decision = _gate.Evaluate(workflow, step, requestedToolName: "run_shell");

        decision.Decision.ShouldBe(Decision.Block);
    }

    [Fact]
    public void Evaluate_ToolOnStepAllowList_Allowed()
    {
        var step = new WorkflowStepDefinition("step-1", "test", [], Kind: WorkflowStepKind.Tool, ToolAllowList: ["read_file"]);
        var workflow = new WorkflowDefinition("wf", "v1", "Test", [step]);

        var decision = _gate.Evaluate(workflow, step, requestedToolName: "read_file");

        decision.Decision.ShouldBe(Decision.Allow);
    }

    [Fact]
    public void Evaluate_StepEffectExceedsWorkflowPolicyMax_Blocked()
    {
        var step = new WorkflowStepDefinition("step-1", "test", [], Kind: WorkflowStepKind.Tool, Effect: EffectClass.Irreversible);
        var policy = new WorkflowPolicyBinding(MaxEffectClass: EffectClass.Mutating);
        var workflow = new WorkflowDefinition("wf", "v1", "Test", [step], Policy: policy);

        var decision = _gate.Evaluate(workflow, step);

        decision.Decision.ShouldBe(Decision.Block);
    }

    [Fact]
    public void Evaluate_StepEffectWithinWorkflowPolicyMax_Allowed()
    {
        var step = new WorkflowStepDefinition("step-1", "test", [], Kind: WorkflowStepKind.Tool, Effect: EffectClass.Additive);
        var policy = new WorkflowPolicyBinding(MaxEffectClass: EffectClass.Mutating);
        var workflow = new WorkflowDefinition("wf", "v1", "Test", [step], Policy: policy);

        var decision = _gate.Evaluate(workflow, step);

        decision.Decision.ShouldBe(Decision.Allow);
    }

    [Fact]
    public void Evaluate_EffectRequiresApprovalPerWorkflowPolicy_RequiresConfirmation()
    {
        var step = new WorkflowStepDefinition("step-1", "test", [], Kind: WorkflowStepKind.Tool, Effect: EffectClass.Mutating);
        var policy = new WorkflowPolicyBinding(MaxEffectClass: EffectClass.Irreversible, RequireApprovalFor: [EffectClass.Mutating, EffectClass.Irreversible]);
        var workflow = new WorkflowDefinition("wf", "v1", "Test", [step], Policy: policy);

        var decision = _gate.Evaluate(workflow, step);

        decision.Decision.ShouldBe(Decision.RequireConfirmation);
    }

    [Fact]
    public void Evaluate_StepDeclaresRequireApprovalAutonomy_RequiresConfirmation()
    {
        var step = new WorkflowStepDefinition("step-1", "test", [], Kind: WorkflowStepKind.Tool, MaxAutonomy: AutonomyMode.RequireApproval);
        var workflow = new WorkflowDefinition("wf", "v1", "Test", [step]);

        var decision = _gate.Evaluate(workflow, step);

        decision.Decision.ShouldBe(Decision.RequireConfirmation);
    }

    [Fact]
    public void Evaluate_UsageExceedsStepCostBudget_Blocked()
    {
        var step = new WorkflowStepDefinition("step-1", "test", [], Kind: WorkflowStepKind.Tool, Budget: new WorkflowBudget(MaxCost: 10m));
        var workflow = new WorkflowDefinition("wf", "v1", "Test", [step]);

        var decision = _gate.Evaluate(workflow, step, usage: new WorkflowBudgetUsage(Cost: 11m));

        decision.Decision.ShouldBe(Decision.Block);
    }

    [Fact]
    public void Evaluate_UsageWithinStepBudget_Allowed()
    {
        var step = new WorkflowStepDefinition("step-1", "test", [], Kind: WorkflowStepKind.Tool, Budget: new WorkflowBudget(MaxCost: 10m, MaxTokens: 1000));
        var workflow = new WorkflowDefinition("wf", "v1", "Test", [step]);

        var decision = _gate.Evaluate(workflow, step, usage: new WorkflowBudgetUsage(Cost: 5m, Tokens: 500));

        decision.Decision.ShouldBe(Decision.Allow);
    }

    [Fact]
    public void Evaluate_UsageExceedsWorkflowInheritedBudgetWhenStepHasNone_Blocked()
    {
        var step = new WorkflowStepDefinition("step-1", "test", [], Kind: WorkflowStepKind.Tool);
        var policy = new WorkflowPolicyBinding(MaxEffectClass: EffectClass.Irreversible, Budget: new WorkflowBudget(MaxTokens: 100));
        var workflow = new WorkflowDefinition("wf", "v1", "Test", [step], Policy: policy);

        var decision = _gate.Evaluate(workflow, step, usage: new WorkflowBudgetUsage(Tokens: 101));

        decision.Decision.ShouldBe(Decision.Block);
    }

    [Fact]
    public void Evaluate_NoConstraintsDeclared_Allowed()
    {
        var step = new WorkflowStepDefinition("step-1", "test", [], Kind: WorkflowStepKind.Tool);
        var workflow = new WorkflowDefinition("wf", "v1", "Test", [step]);

        var decision = _gate.Evaluate(workflow, step);

        decision.Decision.ShouldBe(Decision.Allow);
    }
}

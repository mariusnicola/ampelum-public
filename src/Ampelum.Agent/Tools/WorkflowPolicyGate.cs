using SignalGrid.Ampelum.Agent.Contracts;

namespace SignalGrid.Ampelum.Agent;

/// <summary>
/// Evaluates a workflow step's declared policy surface (tool allow-list, effect class, autonomy, budget)
/// against its workflow's declared <see cref="WorkflowPolicyBinding"/>, independent of any specific tool
/// invocation. This is the workflow-level counterpart to <see cref="PolicyEngine"/>: <see cref="PolicyEngine"/>
/// decides whether one tool invocation may execute; this gate decides whether a step is even permitted to
/// attempt dispatch under its workflow's declared risk/budget constraints. A caller wiring a step's execution
/// to <see cref="IToolDispatcher"/> should call this gate first and only proceed to <see cref="ToolDispatcher"/>
/// when it returns <see cref="Decision.Allow"/> or a resolved confirmation.
/// </summary>
public sealed class WorkflowPolicyGate
{
    /// <summary>
    /// Evaluates whether <paramref name="step"/> may proceed. Order (first match wins, most severe first):
    /// declared <see cref="AutonomyMode.Forbidden"/> hard-blocks; an undeclared or disallowed
    /// <paramref name="requestedToolName"/> against a declared <see cref="WorkflowStepDefinition.ToolAllowList"/>
    /// blocks; an <see cref="WorkflowStepDefinition.Effect"/> exceeding the workflow's declared
    /// <see cref="WorkflowPolicyBinding.MaxEffectClass"/> blocks; a budget declared on the step or inherited
    /// from the workflow policy that <paramref name="usage"/> has already exceeded blocks; an effect class
    /// listed in <see cref="WorkflowPolicyBinding.RequireApprovalFor"/> or a step declaring
    /// <see cref="AutonomyMode.RequireApproval"/> requires confirmation; otherwise allowed.
    /// </summary>
    public PolicyDecision Evaluate(
        WorkflowDefinition workflow,
        WorkflowStepDefinition step,
        string? requestedToolName = null,
        WorkflowBudgetUsage? usage = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(step);

        if (step.MaxAutonomy == AutonomyMode.Forbidden)
            return PolicyDecision.Blocked($"Step '{step.StepId}' declares AutonomyMode.Forbidden and may never execute.");

        if (step.ToolAllowList is { Count: > 0 } allowList && requestedToolName is not null
            && !allowList.Contains(requestedToolName, StringComparer.Ordinal))
            return PolicyDecision.Blocked($"Step '{step.StepId}' does not allow-list tool '{requestedToolName}'.");

        var policy = workflow.Policy;
        if (policy is not null && step.Effect is { } effect && effect > policy.MaxEffectClass)
            return PolicyDecision.Blocked(
                $"Step '{step.StepId}' declares effect '{effect}', exceeding workflow policy max '{policy.MaxEffectClass}'.");

        var budget = step.Budget ?? policy?.Budget;
        if (budget is not null && usage is not null)
        {
            if (budget.MaxCost is { } maxCost && usage.Cost > maxCost)
                return PolicyDecision.Blocked($"Step '{step.StepId}' exceeded its declared cost budget ({usage.Cost} > {maxCost}).");
            if (budget.MaxTokens is { } maxTokens && usage.Tokens > maxTokens)
                return PolicyDecision.Blocked($"Step '{step.StepId}' exceeded its declared token budget ({usage.Tokens} > {maxTokens}).");
            if (budget.MaxDuration is { } maxDuration && usage.Duration > maxDuration)
                return PolicyDecision.Blocked($"Step '{step.StepId}' exceeded its declared duration budget ({usage.Duration} > {maxDuration}).");
        }

        if (policy?.RequireApprovalFor is { Count: > 0 } requireApprovalFor && step.Effect is { } declaredEffect
            && requireApprovalFor.Contains(declaredEffect))
            return PolicyDecision.ConfirmationRequired(
                $"Step '{step.StepId}' declares effect '{declaredEffect}', which this workflow's policy requires approval for.");

        if (step.MaxAutonomy == AutonomyMode.RequireApproval)
            return PolicyDecision.ConfirmationRequired($"Step '{step.StepId}' declares AutonomyMode.RequireApproval.");

        return PolicyDecision.Allowed;
    }
}

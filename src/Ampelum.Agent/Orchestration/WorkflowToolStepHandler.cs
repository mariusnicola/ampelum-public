using System.Text.Json;
using SignalGrid.Ampelum.Agent.Contracts;

namespace SignalGrid.Ampelum.Agent.Orchestration;

/// <summary>
/// Reference handler wiring a <see cref="WorkflowStepDefinition"/> of kind <c>Tool</c> to
/// <see cref="IToolDispatcher"/>, gated by <see cref="WorkflowPolicyGate"/>. Satisfies the "mandatory
/// ToolDispatcher integration" requirement without any change to <see cref="DurableWorkflowRuntime"/>'s
/// claim loop: <see cref="DurableWorkflowNodeContext"/> already carries both the workflow
/// (<see cref="DurableWorkflowNodeContext.Request"/>'s <c>Workflow</c>) and the step
/// (<see cref="DurableWorkflowNodeContext.Step"/>), so no new runtime plumbing was needed to make this
/// possible — this class is the pattern a caller wires into <see cref="DurableWorkflowRuntime.RunAsync"/>'s
/// handler delegate, not a change to the runtime itself.
/// </summary>
/// <remarks>
/// A <see cref="WorkflowPolicyGate"/> decision of <c>RequireConfirmation</c> parks the node: the handler returns
/// <see cref="DurableWorkflowNodeResult.Wait"/> and the execution reports <c>AwaitingConfirmation</c>. A caller
/// delivers the decision with <see cref="DurableWorkflowRuntime.DecideAsync"/> and continues the same execution
/// with <see cref="DurableWorkflowRuntime.ResumeAsync"/>. The next call of this handler then carries the approval in
/// <see cref="DurableWorkflowNodeContext.Decision"/>, skips the confirmation and dispatches the tool with the
/// node's unchanged idempotency key. A rejection fails the node and the execution. A <c>Block</c> decision still
/// fails the step, whatever was decided. This handler does not use <see cref="IToolDispatcher"/>'s own tool-level
/// confirmation store.
/// </remarks>
public sealed class WorkflowToolStepHandler
{
    private readonly IToolDispatcher _dispatcher;
    private readonly WorkflowPolicyGate _policyGate;
    private readonly AgentIdentity _caller;
    private readonly Func<DurableWorkflowNodeContext, IReadOnlyDictionary<string, JsonElement>> _resolveArgs;

    /// <summary>Creates a handler dispatching every step it's given to <paramref name="dispatcher"/> as <paramref name="caller"/>.</summary>
    /// <param name="dispatcher">The tool dispatcher every allowed step is routed through.</param>
    /// <param name="caller">The identity every dispatched invocation is attributed to.</param>
    /// <param name="policyGate">The workflow-level policy gate evaluated before dispatch. Defaults to a new <see cref="WorkflowPolicyGate"/> with no external state.</param>
    /// <param name="resolveArgs">Resolves the tool arguments for a step. Defaults to no arguments.</param>
    public WorkflowToolStepHandler(
        IToolDispatcher dispatcher,
        AgentIdentity caller,
        WorkflowPolicyGate? policyGate = null,
        Func<DurableWorkflowNodeContext, IReadOnlyDictionary<string, JsonElement>>? resolveArgs = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _caller = caller ?? throw new ArgumentNullException(nameof(caller));
        _policyGate = policyGate ?? new WorkflowPolicyGate();
        _resolveArgs = resolveArgs ?? (_ => new Dictionary<string, JsonElement>());
    }

    /// <summary>
    /// Evaluates <paramref name="context"/>'s step against the workflow's declared policy, then — only if
    /// allowed — dispatches it through <see cref="IToolDispatcher"/>. Only handles steps of
    /// <see cref="WorkflowStepKind.Tool"/>; a caller wiring this as (or into) the handler delegate passed to
    /// <see cref="DurableWorkflowRuntime.RunAsync"/> must route non-Tool-kind steps elsewhere first, since
    /// <c>DurableWorkflowRuntime</c> dispatches every declared step kind through the same handler delegate.
    /// </summary>
    public async Task<DurableWorkflowNodeResult> HandleAsync(DurableWorkflowNodeContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var step = context.Step;
        if (step.Kind != WorkflowStepKind.Tool)
            return DurableWorkflowNodeResult.Failure($"Step '{step.StepId}' has kind '{step.Kind}', not Tool; {nameof(WorkflowToolStepHandler)} only handles Tool-kind steps.");
        var toolName = step.StepType;

        var decision = _policyGate.Evaluate(context.Request.Workflow, step, toolName);
        if (decision.Decision == Decision.Block)
            return DurableWorkflowNodeResult.Failure(decision.Reason ?? $"Step '{step.StepId}' is blocked by workflow policy.");
        if (decision.Decision == Decision.RequireConfirmation && context.Decision is not { Approved: true })
            return DurableWorkflowNodeResult.Wait(decision.Reason ?? $"Step '{step.StepId}' requires approval per workflow policy.");

        var invocationContext = new ToolInvocationContext(
            TraceId: context.Request.ExecutionId,
            WorkflowId: context.Request.Workflow.WorkflowId,
            InvocationId: context.IdempotencyKey);
        var result = await _dispatcher.InvokeAsync(toolName, _resolveArgs(context), _caller, invocationContext, ct).ConfigureAwait(false);
        return result.Success
            ? DurableWorkflowNodeResult.Success(result.Output)
            : DurableWorkflowNodeResult.Failure(result.Error);
    }
}

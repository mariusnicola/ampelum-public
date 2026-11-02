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
/// A <see cref="WorkflowPolicyGate"/> decision of <c>RequireConfirmation</c> currently fails the step rather
/// than pausing for an out-of-band approval — there is no confirmation-resolution loop wired between
/// workflow-level policy decisions and <see cref="IToolDispatcher"/>'s own tool-level confirmation store yet.
/// This is a documented, deliberate scope cut (fail-closed, never silently proceed) pending that follow-up
/// work, <b>not</b> a node-local gap: because the failure is non-retryable, <c>DurableWorkflowRuntime</c>
/// takes the whole execution terminal (<c>Failed</c>), and there is currently no way to resume the same
/// execution after an out-of-band approval — a new execution must be started once approved. Building an
/// actual approval-then-resume path is a real follow-up item, not something this handler already provides.
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
        if (decision.Decision == Decision.RequireConfirmation)
            return DurableWorkflowNodeResult.Failure(decision.Reason ?? $"Step '{step.StepId}' requires approval per workflow policy.");

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

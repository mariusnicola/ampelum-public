using System.Text.Json;
using SignalGrid.Ampelum.Agent.Contracts;
using SignalGrid.Ampelum.Agent.Orchestration;
using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests.Orchestration;

public sealed class WorkflowToolStepHandlerTests
{
    private static readonly AgentIdentity Caller = new(
        AgentType: "test-bot",
        InstanceId: Guid.NewGuid().ToString(),
        Scope: [],
        IssuedAt: DateTimeOffset.UtcNow,
        ExpiresAt: DateTimeOffset.UtcNow.AddHours(1));

    [Fact]
    public async Task HandleAsync_NoPolicyConstraints_DispatchesToToolDispatcher()
    {
        var dispatcher = new RecordingToolDispatcher(ToolResultSuccess("ok"));
        var handler = new WorkflowToolStepHandler(dispatcher, Caller);
        var context = MakeContext(new WorkflowStepDefinition("step-1", "read_file", [], Kind: WorkflowStepKind.Tool));

        var result = await handler.HandleAsync(context, TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeTrue();
        dispatcher.InvokedToolName.ShouldBe("read_file");
    }

    [Fact]
    public async Task HandleAsync_StepDeclaresForbiddenAutonomy_NeverReachesDispatcher()
    {
        var dispatcher = new RecordingToolDispatcher(ToolResultSuccess("ok"));
        var handler = new WorkflowToolStepHandler(dispatcher, Caller);
        var context = MakeContext(new WorkflowStepDefinition("step-1", "run_shell", [], Kind: WorkflowStepKind.Tool, MaxAutonomy: AutonomyMode.Forbidden));

        var result = await handler.HandleAsync(context, TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeFalse();
        dispatcher.InvokedToolName.ShouldBeNull();
    }

    [Fact]
    public async Task HandleAsync_ToolNotOnStepAllowList_NeverReachesDispatcher()
    {
        var dispatcher = new RecordingToolDispatcher(ToolResultSuccess("ok"));
        var handler = new WorkflowToolStepHandler(dispatcher, Caller);
        var context = MakeContext(new WorkflowStepDefinition("step-1", "run_shell", [], Kind: WorkflowStepKind.Tool, ToolAllowList: ["read_file"]));

        var result = await handler.HandleAsync(context, TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeFalse();
        dispatcher.InvokedToolName.ShouldBeNull();
    }

    [Fact]
    public async Task HandleAsync_EffectRequiresApproval_FailsClosedRatherThanAutoApproving()
    {
        var dispatcher = new RecordingToolDispatcher(ToolResultSuccess("ok"));
        var policy = new WorkflowPolicyBinding(MaxEffectClass: EffectClass.Irreversible, RequireApprovalFor: [EffectClass.Mutating]);
        var handler = new WorkflowToolStepHandler(dispatcher, Caller);
        var step = new WorkflowStepDefinition("step-1", "delete_record", [], Kind: WorkflowStepKind.Tool, Effect: EffectClass.Mutating);
        var context = MakeContext(step, policy);

        var result = await handler.HandleAsync(context, TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeFalse();
        dispatcher.InvokedToolName.ShouldBeNull();
    }

    [Fact]
    public async Task HandleAsync_NonToolKindStep_FailsRatherThanDispatchingNonsense()
    {
        var dispatcher = new RecordingToolDispatcher(ToolResultSuccess("ok"));
        var handler = new WorkflowToolStepHandler(dispatcher, Caller);
        var context = MakeContext(new WorkflowStepDefinition("step-1", "whatever", [], Kind: WorkflowStepKind.Sequential));

        var result = await handler.HandleAsync(context, TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeFalse();
        dispatcher.InvokedToolName.ShouldBeNull();
    }

    [Fact]
    public async Task HandleAsync_ToolDispatcherFails_PropagatesFailure()
    {
        var dispatcher = new RecordingToolDispatcher(new ToolResult("read_file", false, null, "boom", DateTimeOffset.UtcNow, TimeSpan.Zero));
        var handler = new WorkflowToolStepHandler(dispatcher, Caller);
        var context = MakeContext(new WorkflowStepDefinition("step-1", "read_file", [], Kind: WorkflowStepKind.Tool));

        var result = await handler.HandleAsync(context, TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeFalse();
        result.Detail.ShouldBe("boom");
    }

    private static ToolResult ToolResultSuccess(string output) => new("", true, output, null, DateTimeOffset.UtcNow, TimeSpan.Zero);

    private static DurableWorkflowNodeContext MakeContext(WorkflowStepDefinition step, WorkflowPolicyBinding? policy = null)
    {
        var workflow = new WorkflowDefinition("wf", "v1", "Test", [step], Policy: policy);
        var request = new WorkflowExecutionRequest("exec-1", workflow, "tester", DateTimeOffset.UtcNow);
        return new DurableWorkflowNodeContext(request, step, $"exec-1:{step.StepId}", 1);
    }

    private sealed class RecordingToolDispatcher(ToolResult result) : IToolDispatcher
    {
        public string? InvokedToolName { get; private set; }

        public Task<ToolResult> InvokeAsync(string toolName, IReadOnlyDictionary<string, JsonElement> args, AgentIdentity caller, CancellationToken ct = default) =>
            InvokeAsync(toolName, args, caller, context: null, ct);

        public Task<ToolResult> InvokeAsync(string toolName, IReadOnlyDictionary<string, JsonElement> args, AgentIdentity caller, ToolInvocationContext? context, CancellationToken ct = default)
        {
            InvokedToolName = toolName;
            return Task.FromResult(result);
        }
    }
}

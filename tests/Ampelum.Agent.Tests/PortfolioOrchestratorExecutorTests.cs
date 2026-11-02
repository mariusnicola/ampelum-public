using System.Text.Json;
using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

public sealed class PortfolioOrchestratorExecutorTests
{
    [Fact]
    public async Task RunAsync_NoDispatcherConfiguredAndNotDryRun_FailsLoudRatherThanFabricatingSuccess()
    {
        var registry = new InMemoryProjectRegistry();
        registry.Register(MakeWorkspace());

        var orchestrator = new PortfolioOrchestrator(registry, new RoundRobinScheduler(), new TestStubDecomposer());

        var result = await orchestrator.RunAsync("test intent", new PortfolioOptions { MaxConcurrentProjects = 1, DryRun = false });

        var wsResult = result.CompletedWorkspaces.Single();
        wsResult.Success.ShouldBeFalse();
        wsResult.TaskResults.Single().Error.ShouldNotBeNull().ShouldContain("No IToolDispatcher configured");
    }

    [Fact]
    public async Task RunAsync_TaskLevelFailureWithNoWorkspaceLevelException_OverallSuccessReflectsIt()
    {
        // Regression test: OverallSuccess previously only reflected exceptions thrown out of workspace
        // execution (`failures`), never task-level Success=false results -- a caller checking only this flag
        // would have seen `true` even when every task in every workspace failed via the fail-loud path.
        var registry = new InMemoryProjectRegistry();
        registry.Register(MakeWorkspace());

        var orchestrator = new PortfolioOrchestrator(registry, new RoundRobinScheduler(), new TestStubDecomposer());

        var result = await orchestrator.RunAsync("test intent", new PortfolioOptions { MaxConcurrentProjects = 1, DryRun = false });

        result.FailedWorkspaces.ShouldBeEmpty(); // no exception was thrown -- this is the case OverallSuccess used to miss
        result.OverallSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_DryRun_ReportsSimulatedSuccessLabeledAsDryRun()
    {
        var registry = new InMemoryProjectRegistry();
        registry.Register(MakeWorkspace());

        var orchestrator = new PortfolioOrchestrator(registry, new RoundRobinScheduler(), new TestStubDecomposer());

        var result = await orchestrator.RunAsync("test intent", new PortfolioOptions { MaxConcurrentProjects = 1, DryRun = true });

        var wsResult = result.CompletedWorkspaces.Single();
        wsResult.Success.ShouldBeTrue();
        wsResult.TaskResults.Single().Output.ShouldNotBeNull().ShouldContain("[dry-run]");
    }

    [Fact]
    public async Task RunAsync_DispatcherConfigured_ActuallyDispatchesAndReflectsRealResult()
    {
        var registry = new InMemoryProjectRegistry();
        registry.Register(MakeWorkspace());
        var caller = new AgentIdentity("test-bot", Guid.NewGuid().ToString(), [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var dispatcher = new RecordingDispatcher(new ToolResult("noop", true, "did the thing", null, DateTimeOffset.UtcNow, TimeSpan.Zero));

        var orchestrator = new PortfolioOrchestrator(
            registry, new RoundRobinScheduler(), new TestStubDecomposer(),
            toolDispatcher: dispatcher, callerIdentity: caller);

        var result = await orchestrator.RunAsync("test intent", new PortfolioOptions { MaxConcurrentProjects = 1, DryRun = false });

        dispatcher.InvokedToolName.ShouldBe("noop");
        var wsResult = result.CompletedWorkspaces.Single();
        wsResult.Success.ShouldBeTrue();
        wsResult.TaskResults.Single().Output.ShouldBe("did the thing");
    }

    [Fact]
    public async Task RunAsync_DispatcherReportsFailure_PropagatesFailureRatherThanOverridingIt()
    {
        var registry = new InMemoryProjectRegistry();
        registry.Register(MakeWorkspace());
        var caller = new AgentIdentity("test-bot", Guid.NewGuid().ToString(), [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var dispatcher = new RecordingDispatcher(new ToolResult("noop", false, null, "denied by policy", DateTimeOffset.UtcNow, TimeSpan.Zero));

        var orchestrator = new PortfolioOrchestrator(
            registry, new RoundRobinScheduler(), new TestStubDecomposer(),
            toolDispatcher: dispatcher, callerIdentity: caller);

        var result = await orchestrator.RunAsync("test intent", new PortfolioOptions { MaxConcurrentProjects = 1, DryRun = false });

        var wsResult = result.CompletedWorkspaces.Single();
        wsResult.Success.ShouldBeFalse();
        wsResult.TaskResults.Single().Error.ShouldBe("denied by policy");
    }

    [Fact]
    public void Constructor_ToolDispatcherWithoutCallerIdentity_Throws()
    {
        var registry = new InMemoryProjectRegistry();
        var dispatcher = new RecordingDispatcher(new ToolResult(true, null, null, null));

        Should.Throw<ArgumentException>(() => new PortfolioOrchestrator(
            registry, new RoundRobinScheduler(), new TestStubDecomposer(), toolDispatcher: dispatcher));
    }

    private static WorkspaceContext MakeWorkspace() => new()
    {
        Id = WorkspaceId.New("test"),
        Label = "Test",
        RootPath = Path.GetTempPath(),
        PriorityWeight = 50,
        MaxBlastRadius = new BlastRadiusScope(),
        AllowedToolIds = new HashSet<string> { "noop" },
        TokenStore = new InMemoryTokenStore(),
        SecretStore = new InMemorySecretStore(),
        Skills = new SkillRegistry(),
    };

    private sealed class RecordingDispatcher(ToolResult result) : IToolDispatcher
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

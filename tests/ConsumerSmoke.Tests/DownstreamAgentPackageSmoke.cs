using Shouldly;
using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Agent.Contracts;
using SignalGrid.Ampelum.Agent.Orchestration;
using SignalGrid.Ampelum.Agent.Synthesis;
using SignalGrid.Ampelum.Agent.Tools;
using Xunit;

namespace ConsumerSmoke.Tests;

/// <summary>
/// Downstream, package-only exercise. This project deliberately
/// has no project references: all Agent packages resolve from _packs.
/// </summary>
public sealed class DownstreamAgentPackageSmoke
{
    [Fact]
    public async Task DeclarativeWorkflow_ToolPolicy_PromptCompilation_AndSynthesisPackages_Interoperate()
    {
        var ct = TestContext.Current.CancellationToken;
        var workflow = new WorkflowDefinition(
            "lead-qualification",
            "1.0.0",
            "Lead qualification",
            [new WorkflowStepDefinition("qualify", "score", [])]);
        var request = new WorkflowExecutionRequest("run-1", workflow, "consumer", DateTimeOffset.UtcNow);
        var runtime = new DurableWorkflowRuntime(new InMemoryWorkflowStateStore(), new InMemoryWorkflowRuntimeAuditSink());

        var snapshot = await runtime.RunAsync(request, (_, _) => Task.FromResult(DurableWorkflowNodeResult.Success()), ct);

        snapshot.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        var policy = new ModeAwareToolPolicy();
        var decision = policy.Evaluate(
            new ToolManifest { Name = "read_file", Description = "Read evidence", DangerTier = DangerTier.Safe, Reversibility = Reversibility.Reversible },
            new AgentIdentity("consumer", "session-1", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)),
            new Dictionary<string, object?> { [ModeAwareToolPolicy.ModeArgKey] = "review" });
        decision.Decision.ShouldBe(Decision.Allow);

        var artifact = PromptCompiler.Compile(new PromptCompilationRequest(
            new PromptTemplateDefinition(
                "lead-summary",
                "1.0.0",
                "Summarize {{segment}} using {{tool}}.",
                [new PromptInputSchema("segment", PromptInputKind.Text), new PromptInputSchema("tool", PromptInputKind.Text)],
                new PromptToolPolicyBinding("consumer-policy", "1.0.0", ["read_file"])),
            [new PromptInputValue("tool", PromptInputKind.Text, "read_file"), new PromptInputValue("segment", PromptInputKind.Text, "solar") ]));

        artifact.RenderedPrompt.ShouldBe("Summarize solar using read_file.");
        SynthesisBridge.AmpelumPredictType.FullName.ShouldBe("SignalGrid.Ampelum.Predict");
        SynthesisBridge.AgentIdentityType.ShouldBe(typeof(AgentIdentity));
        typeof(PostgresWorkflowPersistence).Assembly.GetName().Name.ShouldBe("SignalGrid.Ampelum.Agent.Postgres");
    }
}

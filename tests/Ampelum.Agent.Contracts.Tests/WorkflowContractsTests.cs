using SignalGrid.Ampelum.Agent.Contracts;
using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Contracts.Tests;

public sealed class WorkflowContractsTests
{
    [Fact]
    public void WorkflowDefinition_Preserves_Declarative_Step_Graph()
    {
        var workflow = new WorkflowDefinition(
            "lead-enrichment",
            "1.0.0",
            "Lead enrichment",
            [
                new WorkflowStepDefinition("discover", "source-query", []),
                new WorkflowStepDefinition("enrich", "profile-enrichment", ["discover"]),
            ]);

        workflow.Steps[1].DependsOn.ShouldBe(["discover"]);
    }

    [Fact]
    public void WorkflowExecutionSnapshot_Can_Represent_Confirmation_Pause()
    {
        var snapshot = new WorkflowExecutionSnapshot(
            "execution-1",
            WorkflowExecutionStatus.AwaitingConfirmation,
            "contact-prospect",
            DateTimeOffset.Parse("2026-07-27T10:00:00Z"),
            CheckpointReference: "checkpoint-1");

        snapshot.Status.ShouldBe(WorkflowExecutionStatus.AwaitingConfirmation);
    }
}

using SignalGrid.Ampelum.Agent.Contracts;
using SignalGrid.Ampelum.Agent.Orchestration;
using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests.Orchestration;

public sealed class WorkflowMonitorTests
{
    private readonly DefaultWorkflowMonitor _monitor = new();

    [Fact]
    public async Task EvaluateAsync_NoOutcomes_ReturnsZeroSampleWithConcern()
    {
        var health = await _monitor.EvaluateAsync("wf", [], TestContext.Current.CancellationToken);

        health.SampleSize.ShouldBe(0);
        health.SuccessRate.ShouldBe(0);
        health.Concerns.ShouldNotBeNull();
    }

    [Fact]
    public async Task EvaluateAsync_AllSucceeded_FullSuccessRateAndNoLowSuccessConcern()
    {
        OutcomeRecord[] outcomes =
        [
            new("e1", "wf", WorkflowExecutionStatus.Succeeded, DateTimeOffset.UtcNow),
            new("e2", "wf", WorkflowExecutionStatus.Succeeded, DateTimeOffset.UtcNow),
        ];

        var health = await _monitor.EvaluateAsync("wf", outcomes, TestContext.Current.CancellationToken);

        health.SuccessRate.ShouldBe(1.0);
        health.SampleSize.ShouldBe(2);
        health.Concerns.ShouldBeNull();
    }

    [Fact]
    public async Task EvaluateAsync_MajorityFailed_FlagsLowSuccessRateConcern()
    {
        OutcomeRecord[] outcomes =
        [
            new("e1", "wf", WorkflowExecutionStatus.Failed, DateTimeOffset.UtcNow),
            new("e2", "wf", WorkflowExecutionStatus.Failed, DateTimeOffset.UtcNow),
            new("e3", "wf", WorkflowExecutionStatus.Succeeded, DateTimeOffset.UtcNow),
        ];

        var health = await _monitor.EvaluateAsync("wf", outcomes, TestContext.Current.CancellationToken);

        health.SuccessRate.ShouldBe(1.0 / 3, 0.001);
        health.Concerns.ShouldNotBeNull().ShouldContain(c => c.Contains("Success rate"));
    }

    [Fact]
    public async Task EvaluateAsync_CostAndLatencyReported_AveragesComputedOnlyFromReportingOutcomes()
    {
        OutcomeRecord[] outcomes =
        [
            new("e1", "wf", WorkflowExecutionStatus.Succeeded, DateTimeOffset.UtcNow, Cost: 10m, Latency: TimeSpan.FromSeconds(2)),
            new("e2", "wf", WorkflowExecutionStatus.Succeeded, DateTimeOffset.UtcNow, Cost: 20m, Latency: TimeSpan.FromSeconds(4)),
            new("e3", "wf", WorkflowExecutionStatus.Succeeded, DateTimeOffset.UtcNow), // no cost/latency reported
        ];

        var health = await _monitor.EvaluateAsync("wf", outcomes, TestContext.Current.CancellationToken);

        health.AverageCost.ShouldBe(15m);
        health.AverageLatency.ShouldBe(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task EvaluateAsync_NonTerminalOutcomesPresent_ExcludedFromSuccessRateAndFlaggedAsConcern()
    {
        // Regression test: a Running/Queued/AwaitingConfirmation outcome is neither a success nor a failure --
        // folding it into the denominator would misrepresent the success rate.
        OutcomeRecord[] outcomes =
        [
            new("e1", "wf", WorkflowExecutionStatus.Succeeded, DateTimeOffset.UtcNow),
            new("e2", "wf", WorkflowExecutionStatus.Running, DateTimeOffset.UtcNow),
        ];

        var health = await _monitor.EvaluateAsync("wf", outcomes, TestContext.Current.CancellationToken);

        health.SuccessRate.ShouldBe(1.0);
        health.Concerns.ShouldNotBeNull().ShouldContain(c => c.Contains("not terminal"));
    }

    [Fact]
    public async Task EvaluateAsync_RiskEventsRecorded_FlagsConcern()
    {
        OutcomeRecord[] outcomes =
        [
            new("e1", "wf", WorkflowExecutionStatus.Succeeded, DateTimeOffset.UtcNow, RiskEvents: ["budget-exceeded"]),
        ];

        var health = await _monitor.EvaluateAsync("wf", outcomes, TestContext.Current.CancellationToken);

        health.Concerns.ShouldNotBeNull().ShouldContain(c => c.Contains("risk-constraint breach"));
    }
}

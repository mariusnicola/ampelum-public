using SignalGrid.Ampelum.Agent.Contracts;
using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Contracts.Tests;

public sealed class EvaluationContractsTests
{
    [Fact]
    public async Task WorkflowEvaluator_Receives_Immutable_Evidence_Request()
    {
        IWorkflowEvaluator evaluator = new PassingEvaluator();
        var request = new EvaluationRequest(
            "evaluation-1",
            "execution-1",
            "quality-rubric-v1",
            DateTimeOffset.Parse("2026-07-27T10:00:00Z"),
            new Dictionary<string, string> { ["evidence-1"] = "verified" });

        var result = await evaluator.EvaluateAsync(request, TestContext.Current.CancellationToken);

        result.Passed.ShouldBeTrue();
    }

    private sealed class PassingEvaluator : IWorkflowEvaluator
    {
        public ValueTask<EvaluationResult> EvaluateAsync(EvaluationRequest request, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new EvaluationResult(
                request.EvaluationId,
                true,
                1m,
                "Passed",
                DateTimeOffset.Parse("2026-07-27T10:00:01Z")));
        }
    }
}

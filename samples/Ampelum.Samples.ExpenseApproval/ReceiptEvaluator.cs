using System.Globalization;
using System.Text.Json;
using SignalGrid.Ampelum.Agent.Contracts;
using SignalGrid.Ampelum.Agent.Orchestration;

namespace Ampelum.Samples.ExpenseApproval;

/// <summary>
/// Observes one metric for the evaluate step: 1 when the amount on the receipt (the payload of the receipt signal)
/// equals the extracted amount, 0 otherwise. Both come from the snapshots of the step's direct dependencies.
/// </summary>
internal sealed class ReceiptEvaluator : IDurableWorkflowEvaluator
{
    public Task<IReadOnlyList<MetricObservation>> EvaluateAsync(DurableWorkflowEvaluationContext context, CancellationToken ct)
    {
        var steps = context.Request.Workflow.Steps;
        var extractId = steps.First(step => step.Kind == WorkflowStepKind.Sequential).StepId;
        var receiptId = steps.First(step => step.Kind == WorkflowStepKind.WaitForSignal).StepId;
        var expense = JsonSerializer.Deserialize<Expense>(context.Dependencies[extractId].Detail!)!;
        var receipt = context.Dependencies[receiptId].Signal?.Payload;

        var matches = decimal.TryParse(receipt, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) && amount == expense.Amount;
        IReadOnlyList<MetricObservation> observations =
            [new MetricObservation(ExpenseApprovalPipeline.ReceiptMetric, matches ? 1 : 0, DateTimeOffset.UtcNow, nameof(ReceiptEvaluator))];
        return Task.FromResult(observations);
    }
}

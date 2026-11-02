namespace Ampelum.Samples.ExpenseApproval;

/// <summary>An expense extracted from free text. <paramref name="ReviewReason"/> is set when the amount cannot be paid automatically.</summary>
public sealed record Expense(string Vendor, decimal Amount, string Category, string? ReviewReason = null);

/// <summary>Side effects the workflow's two tools perform. A repeated idempotency key must not repeat the effect.</summary>
public interface IExpenseLedger
{
    /// <summary>Pays an expense that is within the limit.</summary>
    Task PayAsync(Expense expense, string idempotencyKey, CancellationToken ct);

    /// <summary>Marks an expense for a human to review, with the expense's review reason if it has one.</summary>
    Task FlagForReviewAsync(Expense expense, string idempotencyKey, CancellationToken ct);
}

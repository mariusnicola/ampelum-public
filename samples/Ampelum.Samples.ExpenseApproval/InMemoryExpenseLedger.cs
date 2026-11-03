using System.Globalization;

namespace Ampelum.Samples.ExpenseApproval;

/// <summary>Ledger that keeps its entries in memory and ignores a repeat of an idempotency key it has already seen.</summary>
public sealed class InMemoryExpenseLedger(Action<string>? log = null) : IExpenseLedger
{
    readonly object _gate = new();
    readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    readonly List<Expense> _paid = [];
    readonly List<Expense> _flagged = [];
    readonly List<Expense> _refunded = [];

    /// <summary>Expenses paid so far.</summary>
    public IReadOnlyList<Expense> Paid { get { lock (_gate) return [.. _paid]; } }

    /// <summary>Expenses flagged for review so far.</summary>
    public IReadOnlyList<Expense> Flagged { get { lock (_gate) return [.. _flagged]; } }

    public Task PayAsync(Expense expense, string idempotencyKey, CancellationToken ct)
    {
        if (Record(idempotencyKey, expense, _paid))
            log?.Invoke($"  [PAY] {Describe(expense)}");
        return Task.CompletedTask;
    }

    /// <summary>Expenses refunded so far.</summary>
    public IReadOnlyList<Expense> Refunded { get { lock (_gate) return [.. _refunded]; } }

    public Task RefundAsync(Expense expense, string idempotencyKey, CancellationToken ct)
    {
        if (Record(idempotencyKey, expense, _refunded))
            log?.Invoke($"  [REFUND] {Describe(expense)}");
        return Task.CompletedTask;
    }

    public Task FlagForReviewAsync(Expense expense, string idempotencyKey, CancellationToken ct)
    {
        if (Record(idempotencyKey, expense, _flagged))
            log?.Invoke($"  [REVIEW] {Describe(expense)}");
        return Task.CompletedTask;
    }

    bool Record(string key, Expense expense, List<Expense> target)
    {
        lock (_gate)
        {
            if (!_seen.Add(key)) return false;
            target.Add(expense);
            return true;
        }
    }

    static string Describe(Expense e) =>
        $"{e.Vendor} {e.Amount.ToString(CultureInfo.InvariantCulture)} ({e.Category})" + (e.ReviewReason is null ? "" : $" reason: {e.ReviewReason}");
}

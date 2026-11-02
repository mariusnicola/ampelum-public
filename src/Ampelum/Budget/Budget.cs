namespace SignalGrid.Ampelum.Budget;

/// <summary>Static factory for the fluent <see cref="IBudget"/> builder.</summary>
public static class Budget
{
    /// <summary>Returns a new fluent builder.</summary>
    public static BudgetBuilder Create() => new();
}

/// <summary>Fluent builder for an <see cref="IBudget"/>.</summary>
public sealed class BudgetBuilder
{
    long? _tokenLimit;
    decimal? _costCentsLimit;
    int? _callLimit;
    TimeSpan? _deadline;

    /// <summary>Sets the maximum total input+output tokens. Null = no limit.</summary>
    public BudgetBuilder WithTokenLimit(long tokens) { _tokenLimit = tokens; return this; }

    /// <summary>Sets the maximum total cost in cents. Null = no limit.</summary>
    public BudgetBuilder WithCostLimit(decimal cents) { _costCentsLimit = cents; return this; }

    /// <summary>Sets the maximum total number of LM calls. Null = no limit.</summary>
    public BudgetBuilder WithCallLimit(int calls) { _callLimit = calls; return this; }

    /// <summary>Sets a wall-clock deadline relative to <see cref="DateTimeOffset.UtcNow"/> at build time.</summary>
    public BudgetBuilder WithDeadline(TimeSpan timeFromNow) { _deadline = timeFromNow; return this; }

    /// <summary>Builds an immutable, thread-safe <see cref="IBudget"/>.</summary>
    public IBudget Build()
    {
        DateTimeOffset? abs = _deadline is { } ts ? DateTimeOffset.UtcNow + ts : null;
        return new BudgetImpl(_tokenLimit, _costCentsLimit, _callLimit, abs);
    }
}

internal sealed class BudgetImpl : IBudget
{
    readonly object _gate = new();
    long _tokensUsed;
    int _callsUsed;
    decimal _costCentsUsed;

    // Reserved counters track in-flight pre-LM reservations; TryReserve checks
    // (used + reserved + delta) atomically under _gate so N parallel callers cannot
    // simultaneously observe "0 used" and over-commit. Release/Consume drain reserved.
    long _tokensReserved;
    int _callsReserved;
    decimal _costCentsReserved;

    public long? TokenLimit { get; }
    public decimal? CostCentsLimit { get; }
    public int? CallLimit { get; }
    public DateTimeOffset? Deadline { get; }

    public BudgetImpl(long? tokenLimit, decimal? costLimit, int? callLimit, DateTimeOffset? deadline)
    {
        TokenLimit = tokenLimit;
        CostCentsLimit = costLimit;
        CallLimit = callLimit;
        Deadline = deadline;
    }

    public void Consume(long tokens, decimal costCents, int calls = 1)
    {
        long nTokens; int nCalls; decimal nCost;
        lock (_gate)
        {
            _tokensUsed += tokens;
            _callsUsed += calls;
            _costCentsUsed += costCents;
            nTokens = _tokensUsed;
            nCalls = _callsUsed;
            nCost = _costCentsUsed;
        }

        if ((TokenLimit is { } tl && nTokens > tl) ||
            (CostCentsLimit is { } cl && nCost > cl) ||
            (CallLimit is { } kl && nCalls > kl) ||
            (Deadline is { } dl && DateTimeOffset.UtcNow >= dl))
        {
            throw new BudgetExceededException(SnapshotInternal(nTokens, nCost, nCalls));
        }
    }

    public bool TryReserve(int calls, long tokens, decimal costCents)
    {
        lock (_gate)
        {
            long projTokens = _tokensUsed + _tokensReserved + tokens;
            int projCalls = _callsUsed + _callsReserved + calls;
            decimal projCost = _costCentsUsed + _costCentsReserved + costCents;
            if ((TokenLimit is { } tl && projTokens > tl) ||
                (CostCentsLimit is { } cl && projCost > cl) ||
                (CallLimit is { } kl && projCalls > kl) ||
                (Deadline is { } dl && DateTimeOffset.UtcNow >= dl))
            {
                return false;
            }
            _tokensReserved += tokens;
            _callsReserved += calls;
            _costCentsReserved += costCents;
            return true;
        }
    }

    public void Release(int calls, long tokens, decimal costCents)
    {
        lock (_gate)
        {
            _tokensReserved -= tokens;
            _callsReserved -= calls;
            _costCentsReserved -= costCents;
        }
    }

    public BudgetSnapshot Snapshot()
    {
        long t; int c; decimal cost;
        lock (_gate) { t = _tokensUsed; c = _callsUsed; cost = _costCentsUsed; }
        return SnapshotInternal(t, cost, c);
    }

    BudgetSnapshot SnapshotInternal(long tokens, decimal cost, int calls) =>
        new(tokens, TokenLimit, cost, CostCentsLimit, calls, CallLimit, Deadline);
}

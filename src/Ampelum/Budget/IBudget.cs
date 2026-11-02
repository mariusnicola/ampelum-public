using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Budget;

/// <summary>
/// Tracks token / cost / call / deadline consumption across one or more parallel module forwards.
/// Implementations must be thread-safe â€” multiple parallel agents share one budget.
/// </summary>
public interface IBudget
{
    /// <summary>Maximum total input+output tokens. Null = no limit.</summary>
    long? TokenLimit { get; }
    /// <summary>Maximum total cost in cents. Null = no limit.</summary>
    decimal? CostCentsLimit { get; }
    /// <summary>Maximum total number of LM calls. Null = no limit.</summary>
    int? CallLimit { get; }
    /// <summary>Wall-clock deadline. Null = no deadline.</summary>
    DateTimeOffset? Deadline { get; }

    /// <summary>
    /// Records consumption from one LM call. Throws <see cref="BudgetExceededException"/>
    /// if any limit is breached after this call. Thread-safe.
    /// </summary>
    void Consume(long tokens, decimal costCents, int calls = 1);

    /// <summary>Returns a snapshot of current usage and limits.</summary>
    BudgetSnapshot Snapshot();

    /// <summary>
    /// Atomically reserves capacity against the budget pre-LM-call. Returns <see langword="true"/>
    /// when (used + reserved + delta) is within all configured limits â€” the caller MUST then
    /// either commit the reservation by pairing <see cref="Consume"/> with <see cref="Release"/>
    /// in the same units, or roll it back via <see cref="Release"/> alone on error.
    /// Returns <see langword="false"/> when the reservation would push any limit over (no
    /// state change on failure). Thread-safe; the default interface implementation falls back
    /// to a non-atomic snapshot check for third-party <see cref="IBudget"/> implementations
    /// that have not yet adopted the reservation API. Net-new in v1.0-rc1 alpha.8 (Gap 5).
    /// </summary>
    [Experimental("AMPELUM_V09_PREVIEW")]
    bool TryReserve(int calls, long tokens, decimal costCents)
    {
        var s = Snapshot();
        if ((TokenLimit is { } tl && s.TokensUsed + tokens > tl) ||
            (CostCentsLimit is { } cl && s.CostCentsUsed + costCents > cl) ||
            (CallLimit is { } kl && s.CallsUsed + calls > kl) ||
            (Deadline is { } dl && DateTimeOffset.UtcNow >= dl))
        {
            return false;
        }
        return true;
    }

    /// <summary>
    /// Releases a previously-acquired reservation without consuming. Used to roll back the
    /// pre-LM-call reservation (a) after <see cref="Consume"/> commits the actual usage, or
    /// (b) when the LM call fails. Thread-safe; the default interface implementation is a
    /// no-op for impls without atomic reservation. Net-new in v1.0-rc1 alpha.8 (Gap 5).
    /// </summary>
    [Experimental("AMPELUM_V09_PREVIEW")]
    void Release(int calls, long tokens, decimal costCents) { }
}

/// <summary>Immutable snapshot of an <see cref="IBudget"/>.</summary>
public sealed record BudgetSnapshot(
    long TokensUsed, long? TokenLimit,
    decimal CostCentsUsed, decimal? CostCentsLimit,
    int CallsUsed, int? CallLimit,
    DateTimeOffset? Deadline);

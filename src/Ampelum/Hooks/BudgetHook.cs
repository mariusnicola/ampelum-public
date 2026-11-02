using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.Budget;

namespace SignalGrid.Ampelum.Hooks;

/// <summary>
/// Hook (Order=0) that enforces an <see cref="IBudget"/> circuit-breaker.
/// <para>
/// <see cref="OnBeforeAsync"/> calls <see cref="IBudget.TryReserve"/> with the caller's expected
/// token / cost / call delta (read from <see cref="HookMetadataKeys.ExpectedPromptTokens"/> /
/// <see cref="HookMetadataKeys.ExpectedCompletionTokens"/> / <see cref="HookMetadataKeys.ExpectedCostCents"/>,
/// falling back to ctor-supplied defaults). On <see langword="false"/> it throws
/// <see cref="BudgetExceededException"/> BEFORE the LM call (Gap 5, v1.0-rc1 alpha.8) â€” this
/// closes the Pareto #11 over-spend window where N parallel forwards all observed CallsUsed=0.
/// </para>
/// <para>
/// <see cref="OnAfterAsync"/> commits the actual usage with <see cref="IBudget.Consume"/>,
/// then releases the prior reservation via <see cref="IBudget.Release"/>. Net effect on the
/// budget is +actual; reserved drops to zero.
/// </para>
/// <para>
/// <see cref="OnErrorAsync"/> releases the reservation (no Consume) so transient LM failures
/// do not erode the budget.
/// </para>
/// Per-scope budgets are looked up via <c>HookContext.Metadata[HookMetadataKeys.BudgetScope]</c>;
/// falls back to global.
/// </summary>
public sealed class BudgetHook : IHook
{
    /// <summary>Internal HookContext key carrying the per-call reservation tuple
    /// (calls, tokens, costCents) so OnAfter / OnError can release it. Implementation detail â€”
    /// not in <see cref="HookMetadataKeys"/> because external hooks have no reason to read it.</summary>
    internal const string ReservationKey = "SignalGrid.Ampelum.budget.reservation";

    readonly IBudget? _global;
    readonly IReadOnlyDictionary<string, IBudget>? _scopes;
    readonly long _defaultPromptTokens;
    readonly long _defaultCompletionTokens;

    /// <summary>Creates a new BudgetHook with the v1.0-rc1 alpha.8 default reservation sizes
    /// (<see cref="HookMetadataKeys.BudgetExpectedPromptTokensDefault"/> /
    /// <see cref="HookMetadataKeys.BudgetExpectedCompletionTokensDefault"/>).</summary>
    /// <param name="global">Global fallback budget. May be null when scope-only.</param>
    /// <param name="scopes">Optional map of scope-key â†’ per-scope budget (e.g. customer ID â†’ budget).</param>
    public BudgetHook(IBudget? global, IReadOnlyDictionary<string, IBudget>? scopes = null)
        : this(global, scopes,
               HookMetadataKeys.BudgetExpectedPromptTokensDefault,
               HookMetadataKeys.BudgetExpectedCompletionTokensDefault)
    {
    }

    /// <summary>Creates a new BudgetHook with caller-tuned default reservation sizes.
    /// Net-new in v1.0-rc1 alpha.8 (Gap 5) â€” use this overload to right-size the pre-LM
    /// <see cref="IBudget.TryReserve"/> reservation for your workload.</summary>
    /// <param name="global">Global fallback budget. May be null when scope-only.</param>
    /// <param name="scopes">Map of scope-key â†’ per-scope budget (e.g. customer ID â†’ budget). May be null.</param>
    /// <param name="defaultExpectedPromptTokens">Reservation default when
    /// <see cref="HookMetadataKeys.ExpectedPromptTokens"/> is absent on the call.</param>
    /// <param name="defaultExpectedCompletionTokens">Reservation default when
    /// <see cref="HookMetadataKeys.ExpectedCompletionTokens"/> is absent.</param>
    public BudgetHook(
        IBudget? global,
        IReadOnlyDictionary<string, IBudget>? scopes,
        long defaultExpectedPromptTokens,
        long defaultExpectedCompletionTokens)
    {
        _global = global;
        _scopes = scopes;
        _defaultPromptTokens = defaultExpectedPromptTokens;
        _defaultCompletionTokens = defaultExpectedCompletionTokens;
    }

    /// <inheritdoc/>
    public int Order => 0;

    /// <inheritdoc/>
    public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct)
    {
        var b = Resolve(ctx);
        if (b is null) return default;

        long promptTokens = ReadLong(ctx, HookMetadataKeys.ExpectedPromptTokens, _defaultPromptTokens);
        long completionTokens = ReadLong(ctx, HookMetadataKeys.ExpectedCompletionTokens, _defaultCompletionTokens);
        decimal costCents = ReadDecimal(ctx, HookMetadataKeys.ExpectedCostCents, 0m);
        long tokens = promptTokens + completionTokens;

#pragma warning disable AMPELUM_V09_PREVIEW // Gap 5: TryReserve is in preview; BudgetHook adopts it now.
        if (!b.TryReserve(calls: 1, tokens: tokens, costCents: costCents))
#pragma warning restore AMPELUM_V09_PREVIEW
        {
            throw new BudgetExceededException(b.Snapshot());
        }
        ctx.Set(ReservationKey, new BudgetReservation(b, 1, tokens, costCents));
        return default;
    }

    /// <inheritdoc/>
    public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct)
    {
        var b = Resolve(ctx);
        if (b is null) return default;
        long tokens = ReadLong(ctx, HookMetadataKeys.PromptTokens, 0L) + ReadLong(ctx, HookMetadataKeys.CompletionTokens, 0L);
        decimal cost = ReadDecimal(ctx, HookMetadataKeys.BudgetCostCents, 0m);
        try
        {
            b.Consume(tokens, cost);
        }
        finally
        {
            ReleaseIfPresent(ctx);
        }
        return default;
    }

    /// <inheritdoc/>
    public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct)
    {
        ReleaseIfPresent(ctx);
        return default;
    }

    static void ReleaseIfPresent(HookContext ctx)
    {
        if (ctx.TryGetValue(ReservationKey, out var raw) && raw is BudgetReservation r)
        {
#pragma warning disable AMPELUM_V09_PREVIEW // Gap 5: Release is in preview; BudgetHook adopts it now.
            r.Budget.Release(r.Calls, r.Tokens, r.CostCents);
#pragma warning restore AMPELUM_V09_PREVIEW
            // Clear the slot so a re-entrant OnError after OnAfter cannot double-release.
            ctx.Set(ReservationKey, null);
        }
    }

    IBudget? Resolve(HookContext ctx)
    {
        if (_scopes is not null
            && ctx.Metadata.TryGetValue(HookMetadataKeys.BudgetScope, out var raw)
            && raw is string scope
            && _scopes.TryGetValue(scope, out var scoped))
        {
            return scoped;
        }
        return _global;
    }

    static long ReadLong(HookContext ctx, string key, long fallback) =>
        ctx.Metadata.TryGetValue(key, out var v) && v is long l ? l : fallback;

    static decimal ReadDecimal(HookContext ctx, string key, decimal fallback) =>
        ctx.Metadata.TryGetValue(key, out var v) && v is decimal d ? d : fallback;

    sealed record BudgetReservation(IBudget Budget, int Calls, long Tokens, decimal CostCents);
}

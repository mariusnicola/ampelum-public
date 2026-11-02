using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.Budget;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum;
using Ampelum.IntegrationTests.Fixtures;
using Ampelum.IntegrationTests.Skips;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using BudgetCls = SignalGrid.Ampelum.Budget.Budget;

namespace Ampelum.IntegrationTests.Pareto;

/// <summary>
/// Pareto integration test #11 (master plan Â§6.H). Budget circuit-breaker exhaustion under
/// parallel forwards: with <c>CallLimit=K</c> and N parallel calls (N&gt;K), exactly K complete
/// successfully and the remaining Nâˆ’K throw <see cref="BudgetExceededException"/> with zero
/// overshoot. The audit chain over the K successful entries verifies end-to-end.
/// </summary>
/// <remarks>
/// alpha.5 promotion (Gap 2): <see cref="AuditEntry.EventType"/> shipped, defaulting to
/// <c>"forward.completed"</c>. The K successful forwards each emit an entry with that
/// discriminator; this test asserts K vs. Nâˆ’K disposition, chain validity, AND that every
/// recorded entry carries the expected <c>forward.completed</c> EventType.
///
/// <para><b>alpha.8 (Gap 5) â€” N=8 / K=3 restoration via <c>IBudget.TryReserve</c>:</b></para>
/// <see cref="BudgetHook.OnBeforeAsync"/> now atomically reserves a call slot via
/// <see cref="IBudget.TryReserve"/>; on a refused reservation it throws
/// <see cref="BudgetExceededException"/> BEFORE the LM call. Under N parallel calls only K
/// reservations succeed and only K LM calls leave the process; the remaining Nâˆ’K short-circuit.
/// The audit chain has K entries (not N) because <see cref="AuditHook"/> (Order=10) runs
/// after BudgetHook (Order=0) on the ascending OnBefore pass, so the throw aborts the call
/// before AuditHook records. Cost: K LM calls â€” kept at K=3 to fit the per-test 5-call cap
/// even though the master plan called for K=20. The N parameter (8) is large enough relative
/// to K to demonstrate the short-circuit; N=64 was the original plan figure but does not
/// change semantics, only contention.
/// </remarks>
public sealed class Pareto11_BudgetExhaustionParallel : IClassFixture<ParetoTestFixture>
{
    readonly ParetoTestFixture _fx;

    /// <summary>xUnit ctor injection.</summary>
    public Pareto11_BudgetExhaustionParallel(ParetoTestFixture fx) => _fx = fx;

    /// <summary>K successes + (Nâˆ’K) BudgetExceeded + audit chain over K entries valid.</summary>
    [Fact]
    [Trait("Cost", "Cheap")]
    public async Task BudgetExhaustion_ParallelN_KCompletes_RemainderThrows()
    {
        Assert.SkipWhen(SkipUnlessRealLm.ShouldSkip, SkipUnlessRealLm.Reason);
        var ct = TestContext.Current.CancellationToken;

        // alpha.8 (Gap 5): BudgetHook.OnBefore now reserves via IBudget.TryReserve, so only K
        // LM calls leave the process even when N parallel forwards race. Cost = K real calls,
        // bounded at K=3 to fit the per-test 5-call cap. N=8 demonstrates ample contention
        // (5 short-circuits) without inflating LM cost.
        const int N = 8;
        const int K = 3;

        using var chat = OpenRouterClientFactory.Create();
        var budget = BudgetCls.Create().WithCallLimit(K).Build();
        var sink = new InMemoryAuditSink();
        var ctx = new AmpelumContext(
            chat,
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance)
            { Hooks = new IHook[] { new BudgetHook(budget), new AuditHook(sink) } };

        var predict = new Predict<QaSig>(ctx);

        int succeeded = 0, exceeded = 0;
        var tasks = Enumerable.Range(0, N).Select(async i =>
        {
            try
            {
                _ = await predict.ForwardAsync(
                    new QaSig { Question = $"Capital of country #{i}? Reply 'X'." },
                    ct: ct);
                Interlocked.Increment(ref succeeded);
            }
            catch (BudgetExceededException)
            {
                Interlocked.Increment(ref exceeded);
            }
        });
        await Task.WhenAll(tasks);

        // BudgetHook (Order=0) reserves a call slot in OnBefore via IBudget.TryReserve.
        // Under N parallel calls only K reservations succeed; the remaining Nâˆ’K throw
        // BudgetExceededException before any LM dispatch.
        succeeded.ShouldBe(K);
        exceeded.ShouldBe(N - K);

        // OnBefore ascending: BudgetHook (Order=0) â†’ AuditHook (Order=10). When BudgetHook
        // throws, the pipeline aborts before AuditHook's OnBefore runs, and OnAfter never
        // fires for that call. So the audit chain has exactly K entries (one per admitted
        // forward), not N â€” this is the post-Gap-5 invariant.
        sink.NextSequence.ShouldBe((long)K);
        var verification = await AuditChainVerifier.VerifyAsync(sink, ct);
        verification.IsValid.ShouldBeTrue(verification.Reason);

        // alpha.5 (Gap 2): every entry carries the default EventType discriminator.
        var entries = new List<AuditEntry>();
        await foreach (var e in sink.EnumerateAsync(ct)) entries.Add(e);
        entries.Count.ShouldBe(K);
        entries.ShouldAllBe(e => e.EventType == "forward.completed");
    }
}

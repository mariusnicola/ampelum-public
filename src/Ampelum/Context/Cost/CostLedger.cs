using System.Runtime.CompilerServices;

namespace SignalGrid.Ampelum.Context.Cost;

/// <summary>Tracks per-turn cost as events in the event store.</summary>
public sealed class CostLedger
{
    private readonly IEventStore _store;

    /// <summary>Creates a ledger backed by the given event store.</summary>
    public CostLedger(IEventStore store) => _store = store;

    /// <summary>Records a cost entry for a turn by appending a cost event.</summary>
    public async ValueTask RecordAsync(int turn, PerTurnCost cost, CancellationToken ct = default)
    {
        await using var w = await _store.AcquireWriterLockAsync(ct);
        var seq = await _store.NextSeqAsync(turn, ct);
        var entry = new CostEvent(turn, seq, DateTimeOffset.UtcNow,
            $"cost:{turn}:{seq}", null,
            cost.InputTokens, cost.OutputTokens, cost.CacheReadTokens, cost.CacheWriteTokens,
            cost.InputCost, cost.OutputCost, cost.CacheReadCost, cost.CacheWriteCost, cost.TotalCost);
        await _store.AppendAsync(entry, ct);
    }

    /// <summary>Reads all cost events for a session.</summary>
    public async IAsyncEnumerable<PerTurnCost> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var e in _store.ReadAsync(new EventQuery(Types: ["CostEvent"]), ct))
        {
            if (e is CostEvent ce)
                yield return new PerTurnCost(ce.Turn, ce.InputTokens, ce.OutputTokens, ce.CacheReadTokens, ce.CacheWriteTokens, ce.InputCost, ce.OutputCost, ce.CacheReadCost, ce.CacheWriteCost, ce.TotalCost);
        }
    }
}

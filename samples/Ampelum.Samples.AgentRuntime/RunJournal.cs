using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Context;
using SignalGrid.Ampelum.Context.Cost;
using SignalGrid.Ampelum.Context.Selection;

namespace Ampelum.Samples.AgentRuntime;

/// <summary>
/// Working memory of one process: what was asked, which files were read and what each model call cost.
/// It lives in an <see cref="InMemoryEventStore"/>, so it does not survive a restart. What must survive
/// is kept by <see cref="PhaseNotes"/> and the workflow checkpoints.
/// </summary>
public sealed class RunJournal : IAsyncDisposable
{
    const string Mode = "triage";

    readonly InMemoryEventStore _store = new();
    readonly CostLedger _ledger;
    readonly HygienePipeline _hygiene = new();
    // The in-memory store allows one writer at a time and refuses a second one, so appends are serialised here.
    readonly SemaphoreSlim _gate = new(1, 1);
    int _turn;

    public RunJournal() => _ledger = new CostLedger(_store);

    /// <summary>Starts the turn the following events belong to.</summary>
    public async Task RecordIntentAsync(string text, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var writer = await _store.AcquireWriterLockAsync(ct).ConfigureAwait(false);
            var seq = await _store.NextSeqAsync(_turn, ct).ConfigureAwait(false);
            await _store.AppendAsync(new UserMessage(_turn, seq, DateTimeOffset.UtcNow, EventKey.Make("user_msg", _turn, seq, text), null, text), ct).ConfigureAwait(false);
            _turn = await _store.CurrentTurnAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Records one file read. A second read of identical content is stored as a pointer, not a copy.</summary>
    public async Task RecordFileReadAsync(string path, string content, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var writer = await _store.AcquireWriterLockAsync(ct).ConfigureAwait(false);
            var callSeq = await _store.NextSeqAsync(_turn, ct).ConfigureAwait(false);
            var call = new ToolCall(_turn, callSeq, DateTimeOffset.UtcNow, EventKey.Make("tool_call", _turn, callSeq, path), null, "read_file", path);
            await _store.AppendAsync(call, ct).ConfigureAwait(false);

            var resultSeq = await _store.NextSeqAsync(_turn, ct).ConfigureAwait(false);
            var result = new SignalGrid.Ampelum.Context.ToolResult(
                _turn, resultSeq, DateTimeOffset.UtcNow, EventKey.Make("tool_result", _turn, resultSeq, content), null,
                call.Key, content, content.Length, ContentHasher.HashHex(content), null, null);
            foreach (var entry in _hygiene.Deduplicate([result]))
                await _store.AppendAsync(entry, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Records the tokens one model call used. No rate table is configured, so the money fields stay zero.</summary>
    public async Task RecordUsageAsync(UsageDetails? usage, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _ledger.RecordAsync(_turn, new PerTurnCost(
                _turn, (int)(usage?.InputTokenCount ?? 0), (int)(usage?.OutputTokenCount ?? 0), 0, 0, 0m, 0m, 0m, 0m, 0m), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Tokens recorded so far.</summary>
    public async Task<long> TotalTokensAsync(CancellationToken ct = default)
    {
        long total = 0;
        await foreach (var cost in _ledger.ReadAllAsync(ct).ConfigureAwait(false))
            total += cost.InputTokens + cost.OutputTokens;
        return total;
    }

    /// <summary>The journal as chat messages, ready to put in front of a model.</summary>
    public async Task<IReadOnlyList<SignalGrid.Ampelum.Context.Selection.ChatMessage>> RenderAsync(CancellationToken ct = default)
    {
        var request = new SelectionRequest(Mode, string.Empty, new HashSet<string>(), new HashSet<string>(), null, null);
        var plan = await new TalkSelector().PlanAsync(_store, request, ct).ConfigureAwait(false);
        return await new DefaultContextRenderer().RenderAsync(plan, plan.Prefix, Mode, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}

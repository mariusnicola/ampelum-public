using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.Host.Persistence;

namespace SignalGrid.Ampelum.Host.Replay;

/// <summary>Provides lookups for audit entries by trace ID or sequence number.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IAuditEntryLookup
{
    /// <summary>
    /// Looks up an audit entry by trace ID: through the index when it has a row, otherwise by reading the sink.
    /// Returns null if not found.
    /// </summary>
    ValueTask<AuditEntry?> TryGetByTraceIdAsync(string traceId, CancellationToken ct);

    /// <summary>Looks up an audit entry by sequence number; returns null if not found.</summary>
    ValueTask<AuditEntry?> TryGetBySequenceAsync(long sequence, CancellationToken ct);
}

/// <summary>Retrieves audit entries from the sink, indexed by trace ID and sequence.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class AuditEntryLookup : IAuditEntryLookup
{
    readonly IAuditSink _sink;
    readonly IAuditIndex _index;

    /// <summary>Initializes a new instance of the AuditEntryLookup class.</summary>
    /// <param name="sink">The audit sink to enumerate entries from.</param>
    /// <param name="index">The audit index for fast lookups by trace ID.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="sink"/> or <paramref name="index"/> is null.</exception>
    public AuditEntryLookup(IAuditSink sink, IAuditIndex index)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(index);
        _sink = sink;
        _index = index;
    }

    /// <inheritdoc/>
    public async ValueTask<AuditEntry?> TryGetByTraceIdAsync(string traceId, CancellationToken ct)
    {
        var row = await _index.TryGetByTraceIdAsync(traceId, ct);
        if (row is not null) return await TryGetBySequenceAsync(row.Sequence, ct);

        // The index only knows entries a rebuild has seen; an entry it lacks is looked for in the sink itself.
        await foreach (var entry in _sink.EnumerateAsync(ct))
            if (ResolveTraceId(entry) == traceId) return entry;
        return null;
    }

    /// <summary>The trace id of an entry: its own, else the one the Host recorded in its inputs, else its sequence.</summary>
    internal static string ResolveTraceId(AuditEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.TraceId))
            return entry.TraceId;

        if (entry.Inputs.TryGetValue("_host.trace_id", out var tidObj)
            && tidObj is string tidStr && !string.IsNullOrEmpty(tidStr))
            return tidStr;

        return $"sequence-{entry.Sequence}";
    }

    /// <inheritdoc/>
    public async ValueTask<AuditEntry?> TryGetBySequenceAsync(long sequence, CancellationToken ct)
    {
        await foreach (var entry in _sink.EnumerateAsync(ct))
            if (entry.Sequence == sequence) return entry;
        return null;
    }
}

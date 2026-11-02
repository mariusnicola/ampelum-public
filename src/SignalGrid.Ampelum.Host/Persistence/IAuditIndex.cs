using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.Persistence;

/// <summary>Read-side index of audit chain entries.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IAuditIndex
{
    /// <summary>Inserts or replaces an audit index row.</summary>
    ValueTask UpsertAsync(AuditIndexRow row, CancellationToken ct);

    /// <summary>Looks up a row by trace ID; returns null if not found.</summary>
    ValueTask<AuditIndexRow?> TryGetByTraceIdAsync(string traceId, CancellationToken ct);

    /// <summary>Returns the total number of indexed entries.</summary>
    ValueTask<long> CountAsync(CancellationToken ct);

    /// <summary>Deletes all rows (used by IndexRebuilder).</summary>
    ValueTask DeleteAllAsync(CancellationToken ct);
}

/// <summary>A row in the audit read-side index.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record AuditIndexRow(
    string TraceId,
    long Sequence,
    DateTimeOffset Timestamp,
    string Module,
    string EventType);

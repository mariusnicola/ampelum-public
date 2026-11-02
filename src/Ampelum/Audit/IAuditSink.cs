namespace SignalGrid.Ampelum.Audit;

/// <summary>
/// Append-only persistent log for <see cref="AuditEntry"/>. Spec Â§4.1.
/// Implementations must guarantee that entries are stored strictly in <c>Sequence</c> order.
/// </summary>
public interface IAuditSink
{
    /// <summary>The next expected sequence number. Sinks reject out-of-order writes.</summary>
    long NextSequence { get; }

    /// <summary>Append a single audit entry. Throws if <paramref name="entry"/>'s sequence does not match <see cref="NextSequence"/>.</summary>
    /// <param name="entry">Hash-chained audit entry produced by <c>AuditHook</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask AppendAsync(AuditEntry entry, CancellationToken ct);

    /// <summary>Enumerate stored entries in sequence order. Implementations may snapshot at call time.</summary>
    /// <param name="ct">Cancellation token observed between yields.</param>
    IAsyncEnumerable<AuditEntry> EnumerateAsync(CancellationToken ct);
}

namespace SignalGrid.Ampelum.Audit;

/// <summary>
/// Implemented by sinks that can name the hash of their newest entry without a full read, including
/// entries that were accepted but are not yet visible to <see cref="IAuditSink.EnumerateAsync"/>.
/// </summary>
internal interface ILastEntryHashSource
{
    /// <summary>The hash of the newest entry, or the zero hash when the sink is empty.</summary>
    ValueTask<byte[]> LastEntryHashAsync(CancellationToken ct);
}

internal static class LastEntryHash
{
    /// <summary>Asks the sink directly when it can answer, otherwise reads it to the end.</summary>
    public static async ValueTask<byte[]> OfAsync(IAuditSink sink, CancellationToken ct)
    {
        if (sink is ILastEntryHashSource source)
            return await source.LastEntryHashAsync(ct).ConfigureAwait(false);
        var last = AuditChainHasher.ZeroHash;
        await foreach (var entry in sink.EnumerateAsync(ct).ConfigureAwait(false))
            last = entry.EntryHash;
        return last;
    }
}

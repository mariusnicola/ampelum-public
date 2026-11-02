using System.Runtime.CompilerServices;
using System.Text.Json;

namespace SignalGrid.Ampelum.Audit;

/// <summary>
/// Append-only single-writer file-backed <see cref="IAuditSink"/>. One JSON object per line.
/// Each <see cref="AppendAsync"/> calls <see cref="FileStream.Flush(bool)"/> with
/// <c>flushToDisk: true</c> for crash-safety. Spec Â§4.5; PSQ-9.
/// Multi-process callers must use a different sink (e.g. PostgresAuditSink, future).
/// </summary>
public sealed class JsonFileAuditSink : IAuditSink, IAsyncDisposable
{
    readonly string _path;
    readonly FileStream _stream;
    readonly object _gate = new();
    long _nextSequence;

    /// <summary>Open or create the audit log at <paramref name="path"/> and recover <see cref="NextSequence"/>.</summary>
    /// <param name="path">Filesystem path. Will be created if missing.</param>
    public JsonFileAuditSink(string path)
    {
        _path = path;
        _nextSequence = CountExistingEntries(path);
        _stream = new FileStream(
            path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite,
            bufferSize: 4096, useAsync: false);
    }

    /// <inheritdoc/>
    public long NextSequence
    {
        get { lock (_gate) return _nextSequence; }
    }

    /// <inheritdoc/>
    public ValueTask AppendAsync(AuditEntry entry, CancellationToken ct)
    {
        lock (_gate)
        {
            if (entry.Sequence != _nextSequence)
                throw new InvalidOperationException(
                    $"Out-of-order audit entry: expected sequence {_nextSequence}, got {entry.Sequence}.");

            var json = JsonSerializer.Serialize(entry, AuditEntryJson.Default.AuditEntry);
            var bytes = System.Text.Encoding.UTF8.GetBytes(json + "\n");
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush(flushToDisk: true);
            _nextSequence++;
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<AuditEntry> EnumerateAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(new FileStream(
            _path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
            yield return JsonSerializer.Deserialize(line, AuditEntryJson.Default.AuditEntry)!;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
    }

    static long CountExistingEntries(string path)
    {
        if (!File.Exists(path)) return 0;
        long count = 0;
        using var reader = new StreamReader(new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        while (reader.ReadLine() is not null) count++;
        return count;
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions]
[System.Text.Json.Serialization.JsonSerializable(typeof(AuditEntry))]
internal sealed partial class AuditEntryJson : System.Text.Json.Serialization.JsonSerializerContext { }

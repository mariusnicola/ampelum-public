using System.Text.Json;
using SignalGrid.Ampelum.Audit;

namespace Ampelum.Samples.AgentService;

/// <summary>
/// A file-backed audit sink whose entries read back as plain values. <see cref="JsonFileAuditSink"/> returns the
/// inputs and outputs of an entry as <see cref="JsonElement"/>, and audit replay refuses those, so replay needs
/// a sink that hands back strings, numbers and booleans.
/// </summary>
public sealed class PlainValueAuditSink(string path) : IAuditSink, IAsyncDisposable
{
    readonly JsonFileAuditSink _file = new(path);

    public long NextSequence => _file.NextSequence;

    public ValueTask AppendAsync(AuditEntry entry, CancellationToken ct) => _file.AppendAsync(entry, ct);

    public async IAsyncEnumerable<AuditEntry> EnumerateAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var entry in _file.EnumerateAsync(ct))
            yield return entry with { Inputs = Plain(entry.Inputs), Outputs = Plain(entry.Outputs) };
    }

    public ValueTask DisposeAsync() => _file.DisposeAsync();

    static Dictionary<string, object?> Plain(IReadOnlyDictionary<string, object?> values) =>
        values.ToDictionary(v => v.Key, v => Plain(v.Value));

    static object? Plain(object? value) => value is not JsonElement element
        ? value
        : element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => element.TryGetInt64(out var whole) ? whole : element.GetDouble(),
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => Plain(p.Value)),
            JsonValueKind.Array => element.EnumerateArray().Select(item => Plain(item)).ToList(),
            _ => null,
        };
}

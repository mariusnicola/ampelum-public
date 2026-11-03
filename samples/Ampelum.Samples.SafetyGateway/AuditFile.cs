using System.Text.Json;
using SignalGrid.Ampelum.Audit;

namespace Ampelum.Samples.SafetyGateway;

/// <summary>
/// Reads an audit file written by <see cref="JsonFileAuditSink"/> back into a form the chain verifier and replay accept.
/// The file sink hands entries back with <see cref="JsonElement"/> values, which the hasher rejects, so each value
/// is turned into the plain string, number, bool, list or dictionary it was written as. The stored hashes are kept.
/// </summary>
public static class AuditFile
{
    public static async Task<IAuditSink> LoadAsync(string path, CancellationToken ct = default)
    {
        var entries = new List<AuditEntry>();
        if (!File.Exists(path))
            return new LoadedAuditSink(entries);

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            var entry = JsonSerializer.Deserialize<AuditEntry>(line)
                ?? throw new InvalidDataException("Audit file holds an empty line.");
            entries.Add(entry with { Inputs = Plain(entry.Inputs), Outputs = Plain(entry.Outputs) });
        }
        return new LoadedAuditSink(entries);
    }

    public static async Task<AuditChainVerificationResult> VerifyAsync(string path, CancellationToken ct = default) =>
        await AuditChainVerifier.VerifyAsync(await LoadAsync(path, ct), ct);

    static IReadOnlyDictionary<string, object?> Plain(IReadOnlyDictionary<string, object?> values) =>
        values.ToDictionary(kv => kv.Key, kv => Plain(kv.Value), StringComparer.Ordinal);

    static object? Plain(object? value) => value is not JsonElement element
        ? value
        : element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var whole) ? whole : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => element.EnumerateArray().Select(item => Plain(item)).ToList(),
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(p => p.Name, p => Plain(p.Value), StringComparer.Ordinal),
            _ => null,
        };
}

/// <summary>
/// Entries read from a file, exactly as stored. Unlike the in-memory sink it does not check the order,
/// so a file with a missing line can still be handed to the chain verifier, which then reports the break.
/// </summary>
sealed class LoadedAuditSink(IReadOnlyList<AuditEntry> entries) : IAuditSink
{
    public long NextSequence => entries.Count;

    public ValueTask AppendAsync(AuditEntry entry, CancellationToken ct) =>
        throw new NotSupportedException("A loaded audit file is read-only.");

    public async IAsyncEnumerable<AuditEntry> EnumerateAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            yield return entry;
        }
        await Task.CompletedTask;
    }
}

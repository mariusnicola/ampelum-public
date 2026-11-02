using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace SignalGrid.Ampelum.Evaluation;

/// <summary>
/// JSONL dataset (one JSON object per line). Re-iterable: each call to <see cref="EnumerateAsync"/>
/// reopens the underlying stream via the supplied factory.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class JsonLineDataset : IDataset
{
    readonly Func<Stream> _streamFactory;
    long? _cachedCount;

    /// <summary>Initialises the dataset from a stream factory invoked on every enumeration.</summary>
    /// <param name="streamFactory">Factory returning a fresh readable <see cref="Stream"/>; called once per enumeration.</param>
    public JsonLineDataset(Func<Stream> streamFactory)
    {
        ArgumentNullException.ThrowIfNull(streamFactory);
        _streamFactory = streamFactory;
    }

    /// <summary>Convenience factory: reads from a file path. Each enumeration reopens the file for read.</summary>
    /// <param name="path">Path to a JSONL file.</param>
    public static JsonLineDataset FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new JsonLineDataset(() => File.OpenRead(path));
    }

    /// <inheritdoc/>
    public long? RowCount => _cachedCount;

    /// <inheritdoc/>
    public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> EnumerateAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        long count = 0;
        using var stream = _streamFactory();
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            var dict = new Dictionary<string, object?>();
            foreach (var prop in doc.RootElement.EnumerateObject())
                dict[prop.Name] = JsonValue(prop.Value);
            count++;
            yield return dict;
        }
        _cachedCount = count;
    }

    static object? JsonValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var i) ? i : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => el.GetRawText(),
    };
}

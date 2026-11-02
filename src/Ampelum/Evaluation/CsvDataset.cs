using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;

namespace SignalGrid.Ampelum.Evaluation;

/// <summary>
/// CSV dataset (header row required). RFC-4180 subset parser: comma delimiter, double-quote
/// escaping. All values typed as <see cref="string"/>. Re-iterable.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class CsvDataset : IDataset
{
    readonly Func<Stream> _streamFactory;
    long? _cachedCount;

    /// <summary>Initialises the dataset from a stream factory invoked on every enumeration.</summary>
    /// <param name="streamFactory">Factory returning a fresh readable <see cref="Stream"/>.</param>
    public CsvDataset(Func<Stream> streamFactory)
    {
        ArgumentNullException.ThrowIfNull(streamFactory);
        _streamFactory = streamFactory;
    }

    /// <summary>Convenience factory: reads from a file path. Each enumeration reopens the file for read.</summary>
    /// <param name="path">Path to a CSV file.</param>
    public static CsvDataset FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new CsvDataset(() => File.OpenRead(path));
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
        var headerLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (headerLine is null) { _cachedCount = 0; yield break; }
        var headers = ParseLine(headerLine);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0) continue;
            var values = ParseLine(line);
            var dict = new Dictionary<string, object?>(headers.Length);
            for (var i = 0; i < headers.Length; i++)
                dict[headers[i]] = i < values.Length ? values[i] : null;
            count++;
            yield return dict;
        }
        _cachedCount = count;
    }

    internal static string[] ParseLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(ch);
            }
            else
            {
                if (ch == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                else if (ch == '"' && sb.Length == 0) inQuotes = true;
                else sb.Append(ch);
            }
        }
        fields.Add(sb.ToString());
        return fields.ToArray();
    }
}

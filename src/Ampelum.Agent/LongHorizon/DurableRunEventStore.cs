using System.Text.Json;

namespace SignalGrid.Ampelum.Agent;

public interface IDurableRunEventStore
{
    Task AppendAsync(DurableRunEvent evt, CancellationToken ct = default);
    Task<IReadOnlyList<DurableRunEvent>> ReadAsync(DurableRunId runId, CancellationToken ct = default);
}

public sealed class JsonFileDurableRunEventStore : IDurableRunEventStore
{
    readonly string _baseDirectory;
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public JsonFileDurableRunEventStore(string? baseDirectory = null)
    {
        _baseDirectory = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ampelum",
            "workflow-events");
        Directory.CreateDirectory(_baseDirectory);
    }

    public Task AppendAsync(DurableRunEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var dir = Path.Combine(_baseDirectory, Sanitize(evt.RunId.Value));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "events.jsonl");
        var json = JsonSerializer.Serialize(evt, JsonOptions);
        File.AppendAllText(path, json + Environment.NewLine, System.Text.Encoding.UTF8);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<DurableRunEvent>> ReadAsync(DurableRunId runId, CancellationToken ct = default)
    {
        var path = Path.Combine(_baseDirectory, Sanitize(runId.Value), "events.jsonl");
        if (!File.Exists(path))
            return Array.Empty<DurableRunEvent>();

        var lines = await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false);
        var events = new List<DurableRunEvent>(lines.Length);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var evt = JsonSerializer.Deserialize<DurableRunEvent>(line, JsonOptions);
            if (evt is not null && string.Equals(evt.RunId.Value, runId.Value, StringComparison.Ordinal))
                events.Add(evt);
        }
        return events;
    }

    static string Sanitize(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');
        return value;
    }
}

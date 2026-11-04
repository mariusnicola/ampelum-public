using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.Host.Contracts;
using Microsoft.Extensions.Options;
using SignalGrid.Ampelum.Host.ModuleRegistry;

namespace SignalGrid.Ampelum.Host.Persistence;

/// <summary>Rebuilds all SQLite read-side indexes from authoritative sources.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class IndexRebuilder : IIndexRebuilder
{
    readonly IHostSqliteConnectionFactory _connectionFactory;
    readonly IAuditIndex _auditIndex;
    readonly IPromptVersionStore _promptStore;
    readonly IOptimizerRunStore _optimizerStore;
    readonly IModuleRegistrySnapshotStore _moduleSnapshot;
    readonly IModuleRegistry _moduleRegistry;
    readonly AmpelumHostOptions _options;
    readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>Initializes the rebuilder with all required dependencies.</summary>
    public IndexRebuilder(
        IHostSqliteConnectionFactory connectionFactory,
        IAuditIndex auditIndex,
        IPromptVersionStore promptStore,
        IOptimizerRunStore optimizerStore,
        IModuleRegistrySnapshotStore moduleSnapshot,
        IModuleRegistry moduleRegistry,
        IOptions<AmpelumHostOptions> options)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(auditIndex);
        ArgumentNullException.ThrowIfNull(promptStore);
        ArgumentNullException.ThrowIfNull(optimizerStore);
        ArgumentNullException.ThrowIfNull(moduleSnapshot);
        ArgumentNullException.ThrowIfNull(moduleRegistry);
        ArgumentNullException.ThrowIfNull(options);

        _connectionFactory = connectionFactory;
        _auditIndex = auditIndex;
        _promptStore = promptStore;
        _optimizerStore = optimizerStore;
        _moduleSnapshot = moduleSnapshot;
        _moduleRegistry = moduleRegistry;
        _options = options.Value;
    }

    /// <inheritdoc/>
    public async ValueTask<RebuildSummary> RebuildAsync(CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            var sw = Stopwatch.StartNew();
            var auditCount = await RebuildAuditIndexAsync(ct);
            var (promptCount, promptFilesSkipped) = await RebuildPromptVersionsAsync(ct);
            await _optimizerStore.DeleteAllAsync(ct);
            var moduleCount = await RebuildModuleSnapshotAsync(ct);
            sw.Stop();
            return new RebuildSummary(auditCount, promptCount, 0, moduleCount, sw.Elapsed)
            {
                PromptFilesSkipped = promptFilesSkipped,
            };
        }
        finally
        {
            _semaphore.Release();
        }
    }

    async ValueTask<int> RebuildAuditIndexAsync(CancellationToken ct)
    {
        await _auditIndex.DeleteAllAsync(ct);
        var auditPath = _options.Storage.AuditFile;
        if (!File.Exists(auditPath)) return 0;

        var count = 0;
        using var reader = new StreamReader(new FileStream(
            auditPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            var entry = JsonSerializer.Deserialize(line, AuditEntryJson.Default.AuditEntry);
            if (entry is null) continue;

            var traceId = entry.TraceId;
            if (string.IsNullOrEmpty(traceId))
                traceId = "sequence-" + entry.Sequence.ToString("D20");

            var row = new AuditIndexRow(traceId, entry.Sequence, entry.Timestamp, entry.ModuleName, entry.EventType);
            await _auditIndex.UpsertAsync(row, ct);
            count++;
        }
        return count;
    }

    sealed record PromptFile(PromptVersionRow Row, DateTimeOffset ActiveSince, string RelativePath);

    async ValueTask<(int Indexed, IReadOnlyList<string> Skipped)> RebuildPromptVersionsAsync(CancellationToken ct)
    {
        // Everything is worked out before the table is touched, and the table is then replaced in one transaction.
        var promptDir = _options.Storage.PromptDir;
        var files = Directory.Exists(promptDir)
            ? Directory.GetFiles(promptDir, "*.json", SearchOption.AllDirectories)
            : [];
        Array.Sort(files, StringComparer.Ordinal);
        var entries = files.Select(f => ReadFile(promptDir, f)).ToList();

        // Version ids are unique in the index. Of the files sharing one, the newest by creation date is kept; on a tie
        // the first by path (the entries are in path order and MaxBy takes the first of equals). The others are reported.
        var kept = new List<PromptFile>();
        var skipped = new List<string>();
        foreach (var group in entries.GroupBy(e => e.Row.VersionId))
        {
            var winner = group.MaxBy(e => e.Row.CreatedAt)!;
            kept.Add(winner);
            skipped.AddRange(group.Where(e => e != winner).Select(e => e.RelativePath));
        }
        kept.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        skipped.Sort(StringComparer.Ordinal);

        // A module has one active version. When files disagree, the one promoted last stays active (a file without
        // "promotedAt" counts from its creation date), the rest are retired.
        var keepActive = kept
            .Where(e => e.Row.Status == PromptStatus.Active)
            .GroupBy(e => e.Row.ModuleName)
            .Select(g => g.MaxBy(e => e.ActiveSince)!.Row.VersionId)
            .ToHashSet();
        var rows = kept
            .Select(e => e.Row.Status == PromptStatus.Active && !keepActive.Contains(e.Row.VersionId)
                ? e.Row with { Status = PromptStatus.Retired }
                : e.Row)
            .ToList();

        await _promptStore.ReplaceAllAsync(rows, ct);
        return (rows.Count, skipped);
    }

    /// <summary>
    /// The row a prompt file stands for: its "_meta" block when it has a readable one, otherwise its place on disk.
    /// POST /prompts/{module} writes {PromptDir}/{module}/{versionId}.json, so without "_meta" the module is the first
    /// folder under the prompt folder and a file directly in the prompt folder has no module to name.
    /// </summary>
    static PromptFile ReadFile(string promptDir, string filePath)
    {
        var relativePath = Path.GetRelativePath(promptDir, filePath);
        var parts = relativePath.Split(Path.DirectorySeparatorChar, 2);
        var versionId = Path.GetFileNameWithoutExtension(filePath);
        var moduleName = parts.Length == 2 ? parts[0] : "unknown";
        var status = PromptStatus.Challenger;
        var createdAt = new DateTimeOffset(File.GetLastWriteTimeUtc(filePath), TimeSpan.Zero);
        string? parentVersionId = null, note = null;
        DateTimeOffset? promotedAt = null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(filePath));
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
            {
                versionId = Text(meta, "versionId") ?? versionId;
                moduleName = Text(meta, "moduleName") ?? moduleName;
                if (Text(meta, "status") is { } s && PromptStatus.All.Contains(s)) status = s;
                if (Text(meta, "createdAt") is { } c && DateTimeOffset.TryParse(c, out var parsed)) createdAt = parsed;
                if (Text(meta, "promotedAt") is { } p && DateTimeOffset.TryParse(p, out var promoted)) promotedAt = promoted;
                parentVersionId = Text(meta, "parentVersionId");
                note = Text(meta, "note");
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A file that cannot be read as JSON still gets its row from its place on disk.
        }

        return new PromptFile(
            new PromptVersionRow(versionId, moduleName, status, createdAt, parentVersionId, filePath, note),
            promotedAt ?? createdAt,
            relativePath);
    }

    static string? Text(JsonElement meta, string name) =>
        meta.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrEmpty(value.GetString())
            ? value.GetString()
            : null;

    async ValueTask<int> RebuildModuleSnapshotAsync(CancellationToken ct)
    {
        await _moduleSnapshot.DeleteAllAsync(ct);
        var names = _moduleRegistry.Names;
        var count = 0;

        foreach (var name in names)
        {
            if (!_moduleRegistry.TryGet(name, out var slot)) continue;

            var row = new ModuleSnapshotRow(name, "unknown", slot.SupportsStreaming, DateTimeOffset.UtcNow);
            await _moduleSnapshot.UpsertAsync(row, ct);
            count++;
        }
        return count;
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions]
[System.Text.Json.Serialization.JsonSerializable(typeof(AuditEntry))]
internal sealed partial class AuditEntryJson : System.Text.Json.Serialization.JsonSerializerContext { }

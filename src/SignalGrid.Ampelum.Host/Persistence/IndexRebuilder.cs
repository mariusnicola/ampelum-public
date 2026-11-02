using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using SignalGrid.Ampelum.Audit;
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
            var promptCount = await RebuildPromptVersionsAsync(ct);
            await _optimizerStore.DeleteAllAsync(ct);
            var moduleCount = await RebuildModuleSnapshotAsync(ct);
            sw.Stop();
            return new RebuildSummary(auditCount, promptCount, 0, moduleCount, sw.Elapsed);
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

    async ValueTask<int> RebuildPromptVersionsAsync(CancellationToken ct)
    {
        await _promptStore.DeleteAllAsync(ct);
        var promptDir = _options.Storage.PromptDir;
        if (!Directory.Exists(promptDir)) return 0;

        var count = 0;
        var files = Directory.GetFiles(promptDir, "*.json");
        foreach (var filePath in files)
        {
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            var row = new PromptVersionRow(
                fileName,
                "unknown",
                "challenger",
                DateTimeOffset.UtcNow,
                null,
                filePath,
                null);
            await _promptStore.InsertChallengerAsync(row, ct);
            count++;
        }
        return count;
    }

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

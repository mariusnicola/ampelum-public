using System.Collections.Concurrent;
using System.Threading.Channels;

namespace SignalGrid.Ampelum.Agent.CodeIntelligence;

/// <summary>
/// Orchestrates the full background indexing pipeline.
/// StartAsync returns immediately — the user can work while indexing happens.
/// A continuous background loop re-processes files as the watcher detects changes.
/// </summary>
public sealed class IndexerPipeline : IDisposable
{
    private readonly string _workingDir;
    private readonly CodeIndexer _indexer = new();
    private readonly IndexStore _store;
    private readonly EmbeddingEngine _engine = new();
    private readonly IndexWatcher _watcher;
    private readonly MethodSummarizer? _summarizer;
    private readonly ContextAssembler _assembler;

    // Channel that feeds changed file paths into the background worker
    private readonly Channel<string[]> _workChannel = Channel.CreateUnbounded<string[]>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource _cts = new();
    private Task? _backgroundTask;
    private bool _disposed;

    // ETA tracking
    private int _totalSymbols;
    private int _doneSymbols;
    private DateTime _scanStart = DateTime.UtcNow;

    public event Action<IndexProgress>? OnProgress;
    public bool IsWarm => _store.TotalSymbols > 0;
    public ContextAssembler Assembler => _assembler;
    public IndexStats Stats => _store.GetStats();

    public IndexerPipeline(string workingDir, Func<string, string, Task<string?>>? summarizeFunc = null)
    {
        _workingDir = workingDir;
        _store = new IndexStore(workingDir);
        _assembler = new ContextAssembler(_store, _engine, workingDir);

        if (summarizeFunc is not null)
        {
            _summarizer = new MethodSummarizer(summarizeFunc);
            _summarizer.OnSummarized += OnSymbolSummarized;
        }

        // Wire watcher events into the work channel
        _watcher = new IndexWatcher(workingDir);
        _watcher.OnFilesChanged += files => _workChannel.Writer.TryWrite(files);
        _watcher.OnRescanNeeded += () => _workChannel.Writer.TryWrite([]); // empty = full rescan
    }

    /// <summary>
    /// Starts background indexing and returns immediately.
    /// The caller can start accepting user input right away.
    /// </summary>
    public Task StartAsync(CancellationToken ct)
    {
        _watcher.Start();
        if (_summarizer is not null)
            _ = _summarizer.StartAsync();

        _backgroundTask = Task.Run(() => BackgroundLoopAsync(_cts.Token), _cts.Token);

        // Seed an initial full scan
        _workChannel.Writer.TryWrite([]);

        return Task.CompletedTask;
    }

    /// <summary>Pre-turn context assembly. Returns empty if index is cold.</summary>
    public AssembledContext AssembleContext(string query, int tokenBudget = 2000)
    {
        if (!IsWarm) return new AssembledContext();
        return _assembler.Assemble(query);
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        _workChannel.Writer.TryComplete();
        if (_backgroundTask is not null)
        {
            try { await _backgroundTask.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch { /* best-effort drain */ }
        }
    }

    // ── background loop ───────────────────────────────────────────────────────

    private async Task BackgroundLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var files in _workChannel.Reader.ReadAllAsync(ct))
            {
                if (ct.IsCancellationRequested) break;

                if (files.Length == 0)
                    await FullScanAsync(ct);
                else
                    await ProcessFilesAsync(files, ct);

                // Check for any drift the watcher may have missed
                var drift = _watcher.CheckForDrift(_store.GetAllSymbols().Select(s => s.File).Distinct());
                if (drift.Length > 0)
                    await ProcessFilesAsync(drift, ct);
            }
        }
        catch (OperationCanceledException) { /* clean exit */ }
        catch (Exception ex)
        {
            // Never crash the background loop — log and continue
            System.Diagnostics.Debug.WriteLine($"[IndexerPipeline] background error: {ex.Message}");
        }
    }

    private async Task FullScanAsync(CancellationToken ct)
    {
        _scanStart = DateTime.UtcNow;

        var results = await Task.Run(
            () => _indexer.ExtractDirectory(_workingDir), ct);

        var allSymbols = results.SelectMany(r => r.Symbols).ToArray();
        _totalSymbols = allSymbols.Length;
        _doneSymbols  = 0;

        // Persist all symbols in one batch transaction
        var batches = results
            .Where(r => r.Error is null)
            .Select(r => (r.File, r.Symbols))
            .ToArray();

        await Task.Run(() => _store.BatchUpsert(batches), ct);

        // Embed + summarize each symbol
        foreach (var sym in allSymbols)
        {
            if (ct.IsCancellationRequested) break;
            await ProcessSymbolAsync(sym, ct);
            _watcher.MarkIndexed(sym.File);
        }

        EmitProgress("ready");
    }

    private async Task ProcessFilesAsync(string[] files, CancellationToken ct)
    {
        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;

            if (!File.Exists(file))
            {
                await Task.Run(() => _store.RemoveFile(file), ct);
                continue;
            }

            var result = await Task.Run(() => _indexer.Extract(file), ct);
            if (result.Error is null)
            {
                await Task.Run(() => _store.UpsertFile(file, result.Symbols), ct);
                foreach (var sym in result.Symbols)
                    await ProcessSymbolAsync(sym, ct);
                _watcher.MarkIndexed(file);
            }
        }

        EmitProgress();
    }

    private async Task ProcessSymbolAsync(Symbol sym, CancellationToken ct)
    {
        // If summarizer is available, enqueue for async summary+embed; otherwise embed now
        if (_summarizer is not null && sym.NeedsSummary)
        {
            _summarizer.Enqueue(sym);
        }
        else
        {
            var text = sym.EmbeddingText;
            var embedding = await Task.Run(() => _engine.Embed(text), ct);
            if (embedding is not null)
                await Task.Run(
                    () => _store.UpdateEmbedding(sym.File, sym.Name, sym.LineStart,
                        EmbeddingEngine.FloatArrayToBytes(embedding)), ct);
        }

        Interlocked.Increment(ref _doneSymbols);
        EmitProgress();
    }

    private void OnSymbolSummarized(Symbol sym)
    {
        _store.UpdateSummary(sym.File, sym.Name, sym.LineStart, sym.Summary);
        var embedding = _engine.Embed(sym.EmbeddingText);
        if (embedding is not null)
            _store.UpdateEmbedding(sym.File, sym.Name, sym.LineStart,
                EmbeddingEngine.FloatArrayToBytes(embedding));
    }

    private void EmitProgress(string phase = "scanning")
    {
        var total = _totalSymbols;
        var done  = Math.Min(_doneSymbols, total);
        TimeSpan? eta = null;

        if (done > 0 && total > done && phase != "ready")
        {
            var elapsed = DateTime.UtcNow - _scanStart;
            var rate    = elapsed.TotalSeconds / done;          // seconds per symbol
            eta = TimeSpan.FromSeconds(rate * (total - done));
        }

        OnProgress?.Invoke(new IndexProgress(done, total, Phase: phase, Eta: eta));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _store.Dispose();
        _watcher.Dispose();
        _summarizer?.Dispose();
        _cts.Dispose();
    }
}

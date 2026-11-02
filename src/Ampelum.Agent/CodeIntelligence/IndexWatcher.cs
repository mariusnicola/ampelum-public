using System.Collections.Concurrent;

namespace SignalGrid.Ampelum.Agent.CodeIntelligence;

public sealed class IndexWatcher : IDisposable
{
    private readonly string _workingDir;
    private readonly string[] _ignorePatterns;
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private readonly ConcurrentBag<string> _pendingFiles = [];
    private readonly Dictionary<string, DateTime> _fileMtimes = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;
    private const int DebounceMs = 500;

    public event Action<string[]>? OnFilesChanged;
    public event Action? OnRescanNeeded;

    public IndexWatcher(string workingDir, string[]? ignorePatterns = null)
    {
        _workingDir = workingDir;
        _ignorePatterns = ignorePatterns ?? ["bin", "obj", "node_modules", ".git", ".ampelum", "dist", "build"];
    }

    public void Start()
    {
        if (_watcher is not null) return;
        _watcher = new FileSystemWatcher(_workingDir)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
            IncludeSubdirectories = true
        };
        _watcher.Created += OnChanged;
        _watcher.Changed += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Deleted += OnDeleted;
        _watcher.Error += OnError;
        _watcher.EnableRaisingEvents = true;
        _debounceTimer = new Timer(_ => FlushPending(), null, DebounceMs, DebounceMs);
    }

    public string[] CheckForDrift(IEnumerable<string> indexedFiles)
    {
        var changed = new List<string>();
        foreach (var file in indexedFiles)
        {
            if (!File.Exists(file)) { changed.Add(file); continue; }
            var mtime = File.GetLastWriteTimeUtc(file);
            if (_fileMtimes.TryGetValue(file, out var lastMtime) && mtime > lastMtime)
            {
                _fileMtimes[file] = mtime;
                if (CodeIndexer.IsSupported(file)) changed.Add(file);
            }
            else { _fileMtimes[file] = mtime; }
        }
        return [.. changed];
    }

    public void MarkIndexed(string file) { try { _fileMtimes[file] = File.GetLastWriteTimeUtc(file); } catch { } }
    public void ClearPending() { while (_pendingFiles.TryTake(out _)) { } }

    private void OnChanged(object sender, FileSystemEventArgs e) { if (!ShouldIgnore(e.FullPath)) _pendingFiles.Add(e.FullPath); }
    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (!ShouldIgnore(e.FullPath)) _pendingFiles.Add(e.FullPath);
        if (!ShouldIgnore(e.OldFullPath)) _pendingFiles.Add(e.OldFullPath);
    }
    private void OnDeleted(object sender, FileSystemEventArgs e) { if (!ShouldIgnore(e.FullPath)) _pendingFiles.Add(e.FullPath); }
    private void OnError(object sender, ErrorEventArgs e) { OnRescanNeeded?.Invoke(); }

    private void FlushPending()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (_pendingFiles.TryTake(out var f))
        {
            if (CodeIndexer.IsSupported(f) || !File.Exists(f)) files.Add(f);
        }
        if (files.Count > 0) OnFilesChanged?.Invoke([.. files]);
    }

    private bool ShouldIgnore(string path) => _ignorePatterns.Any(p => path.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watcher?.Dispose();
        _debounceTimer?.Dispose();
    }
}

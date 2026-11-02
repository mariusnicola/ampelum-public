using System.Text.Json;
using SignalGrid.Ampelum.Caching;
using SignalGrid.Ampelum.Usage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace SignalGrid.Ampelum.Testing;

/// <summary>
/// Base fixture for usecase E2E tests. Wires a real IChatClient with a
/// DefaultUsageTracker. On disposal, writes usage JSON to the output directory
/// and to <c>tools/usage-reports/</c> for aggregation.
/// </summary>
public abstract class E2EFixture : IAsyncDisposable
{
    private readonly DefaultUsageTracker _usage = new();
    private IDistributedCache? _cache;
    private AmpelumCachingChatClient? _cachedChat;

    /// <summary>Project name used in the usage report.</summary>
    protected abstract string ProjectName { get; }

    /// <summary>Model identifier for the report.</summary>
    protected abstract string ModelId { get; }

    /// <summary>The real IChatClient, set by subclass during construction.</summary>
    protected abstract IChatClient Chat { get; }

    /// <summary>Usage tracker for the test run.</summary>
    public DefaultUsageTracker Usage => _usage;

    /// <summary>
    /// Creates an <see cref="AmpelumContext"/> with the real client,
    /// ChatAdapter, and usage tracker pre-wired.
    /// </summary>
    public AmpelumContext CreateContext()
    {
        return new AmpelumContext(Chat, new JsonAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
        {
            Usage = _usage,
        };
    }

    /// <summary>
    /// Creates an <see cref="AmpelumContext"/> with the <see cref="Chat"/> wrapped
    /// in an <see cref="AmpelumCachingChatClient"/>. Backend selected by
    /// <c>E2E_CACHE_BACKEND</c> env var: <c>"disk"</c> → <see cref="FileDistributedCache"/>,
    /// otherwise <see cref="MemoryDistributedCache"/>. Repeated identical prompts
    /// return from cache, incrementing <see cref="Usage"/> cache hits.
    /// </summary>
    public AmpelumContext CreateCachingContext()
    {
        _cache ??= CreateCacheBackend();
        _cachedChat ??= new AmpelumCachingChatClient(Chat, _cache);
        E2ETrace.Log($"CreateCachingContext → AmpelumCachingChatClient wrapping real IChatClient");
        return new AmpelumContext(_cachedChat, new JsonAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
        {
            Usage = _usage,
        };
    }

    private IDistributedCache CreateCacheBackend()
    {
        var backend = (Environment.GetEnvironmentVariable("E2E_CACHE_BACKEND") ?? "").Trim();
        E2ETrace.Log($"Env E2E_CACHE_BACKEND='{backend}' (len={backend.Length})");
        if (string.Equals(backend, "memory", StringComparison.OrdinalIgnoreCase))
        {
            E2ETrace.Log($"CacheBackend=MEMORY (explicit)");
            return new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        }
        // Default: disk-backed cache — survives dotnet test sessions
        var repoRoot = FindRepoRoot() ?? AppContext.BaseDirectory;
        var dir = Path.Combine(repoRoot, "tools", "ampelum-e2e-cache");
        E2ETrace.Log($"CacheBackend=DISK (default) dir={dir}");
        return new FileDistributedCache(dir);
    }

    /// <summary>
    /// Writes usage JSON. Override in subclass to call at dispose.
    /// </summary>
    public virtual async ValueTask DisposeAsync()
    {
        var snapshot = _usage.Snapshot();
        var report = new
        {
            project = ProjectName,
            model = ModelId,
            summary = snapshot,
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
        };

        var dir = GetAssemblyDirectory() ?? Environment.CurrentDirectory;
        var path = Path.Combine(dir, $"test-usage-{ProjectName.ToLowerInvariant()}.json");

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json);

        var repoRoot = FindRepoRoot();
        if (repoRoot != null)
        {
            var repoPath = Path.Combine(repoRoot, "tools", "usage-reports", $"test-usage-{ProjectName.ToLowerInvariant()}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(repoPath)!);
            await File.WriteAllTextAsync(repoPath, json);
        }
    }

    /// <summary>
    /// Returns the assembly directory for usage output. Overridable for branch tests.
    /// </summary>
    protected virtual string? GetAssemblyDirectory() =>
        Path.GetDirectoryName(typeof(E2EFixture).Assembly.Location);

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(Environment.CurrentDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SignalGrid.Ampelum.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}

using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using SignalGrid.Ampelum;

namespace SignalGrid.Ampelum.Testing;

/// <summary>
/// A simple file-based <see cref="IDistributedCache"/> that persists cache entries
/// as JSON files in a directory. Survives process restarts, enabling cross-session
/// cache sharing for E2E tests.
/// </summary>
public sealed class FileDistributedCache : IDistributedCache
{
    private readonly string _directory;

    /// <summary>
    /// Creates a file-backed cache in <paramref name="directory"/>.
    /// The directory is created if it doesn't exist.
    /// </summary>
    public FileDistributedCache(string directory)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        System.IO.Directory.CreateDirectory(_directory);
        E2ETrace.Log($"FileDistributedCache CTOR directory={_directory}");
    }

    /// <inheritdoc/>
    public byte[]? Get(string key) => GetAsync(key).GetAwaiter().GetResult();

    /// <inheritdoc/>
    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        var path = KeyToPath(key);
        E2ETrace.Log($"GET {key} -> path={path}");

        if (!File.Exists(path))
        {
            E2ETrace.Log($"  MISS (file not found)");
            return null;
        }

        CacheEntry? entry;
        try
        {
            var json = await File.ReadAllTextAsync(path, token).ConfigureAwait(false);
            entry = JsonSerializer.Deserialize<CacheEntry>(json);
        }
        catch
        {
            E2ETrace.Log($"  MISS (corrupt entry)");
            SafeDelete(path);
            return null;
        }

        if (entry is null)
        {
            E2ETrace.Log($"  MISS (null entry)");
            SafeDelete(path);
            return null;
        }

        // Check expiry (absolute)
        if (entry.AbsoluteExpiration is DateTimeOffset exp && exp <= DateTimeOffset.UtcNow)
        {
            E2ETrace.Log($"  EXPIRED (absolute {exp})");
            SafeDelete(path);
            return null;
        }

        // Check sliding expiry
        if (entry.LastAccessed is DateTimeOffset last && entry.SlidingExpiration is TimeSpan sliding)
        {
            if (last + sliding <= DateTimeOffset.UtcNow)
            {
                E2ETrace.Log($"  EXPIRED (sliding {last}+{sliding})");
                SafeDelete(path);
                return null;
            }
            // Touch to extend sliding window
            entry.LastAccessed = DateTimeOffset.UtcNow;
            await WriteEntryAsync(path, entry, token).ConfigureAwait(false);
        }

        E2ETrace.Log($"  HIT (len={entry.Value.Length})");
        return Convert.FromBase64String(entry.Value);
    }

    /// <inheritdoc/>
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        => SetAsync(key, value, options).GetAwaiter().GetResult();

    /// <inheritdoc/>
    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options,
        CancellationToken token = default)
    {
        var entry = new CacheEntry
        {
            Value = Convert.ToBase64String(value),
            AbsoluteExpiration = options.AbsoluteExpiration,
            SlidingExpiration = options.SlidingExpiration,
            LastAccessed = DateTimeOffset.UtcNow,
        };
        var path = KeyToPath(key);
        E2ETrace.Log($"SET {key} -> {path} (valuelen={value.Length})");
        return WriteEntryAsync(path, entry, token);
    }

    /// <inheritdoc/>
    public void Refresh(string key) => RefreshAsync(key).GetAwaiter().GetResult();

    /// <inheritdoc/>
    public async Task RefreshAsync(string key, CancellationToken token = default)
    {
        var path = KeyToPath(key);
        if (!File.Exists(path)) return;

        CacheEntry? entry;
        try
        {
            var json = await File.ReadAllTextAsync(path, token).ConfigureAwait(false);
            entry = JsonSerializer.Deserialize<CacheEntry>(json);
        }
        catch
        {
            SafeDelete(path);
            return;
        }
        if (entry is null)
        {
            SafeDelete(path);
            return;
        }

        entry.LastAccessed = DateTimeOffset.UtcNow;
        await WriteEntryAsync(path, entry, token).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Remove(string key) => RemoveAsync(key).GetAwaiter().GetResult();

    /// <inheritdoc/>
    public Task RemoveAsync(string key, CancellationToken token = default)
    {
        SafeDelete(KeyToPath(key));
        return Task.CompletedTask;
    }

    private string KeyToPath(string key)
    {
        // Sanitize key for filesystem
        var safe = string.Join("_", key.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(_directory, safe + ".json");
    }

    private static async Task WriteEntryAsync(string path, CacheEntry entry,
        CancellationToken token)
    {
        var json = JsonSerializer.Serialize(entry);
        E2ETrace.Log($"  WRITE {path} ({json.Length} chars)");
        await File.WriteAllTextAsync(path, json, token).ConfigureAwait(false);
    }

    private static void SafeDelete(string path)
    {
        try { File.Delete(path); } catch { /* best-effort */ }
    }

    private sealed class CacheEntry
    {
        public string Value { get; set; } = "";
        public DateTimeOffset? AbsoluteExpiration { get; set; }
        public TimeSpan? SlidingExpiration { get; set; }
        public DateTimeOffset? LastAccessed { get; set; }
    }
}

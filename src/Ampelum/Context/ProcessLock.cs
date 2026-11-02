using System.Diagnostics;

namespace SignalGrid.Ampelum.Context;

/// <summary>Thrown when a ProcessLock cannot be acquired because another process holds it.</summary>
public sealed class ProcessLockHeldException : Exception
{
    /// <summary>PID of the process holding the lock.</summary>
    public int HoldingPid { get; }
    /// <summary>ISO-8601 timestamp when the lock was acquired.</summary>
    public string StartedAt { get; }

    /// <summary>
    /// Creates a new ProcessLockHeldException. <paramref name="hint"/> lets a caller append
    /// product-specific guidance (e.g. a command to run) to the generic message; when omitted,
    /// the message stays generic with no caller-specific product name or command.
    /// </summary>
    public ProcessLockHeldException(int pid, string startedAt, string? hint = null)
        : base($"Session locked by PID {pid} (started {startedAt}); another instance is already running." +
               (string.IsNullOrEmpty(hint) ? "" : $" {hint}"))
    { HoldingPid = pid; StartedAt = startedAt; }
}

/// <summary>File-based process lock with stale-PID detection. Deletes lock file on dispose.</summary>
public sealed class ProcessLock : IAsyncDisposable
{
    private readonly FileStream _stream;
    private readonly string _path;

    private ProcessLock(FileStream s, string path) { _stream = s; _path = path; }

    /// <summary>
    /// Acquires a lock on the session directory. Throws ProcessLockHeldException if held by a live
    /// process. <paramref name="hint"/> is an optional caller-supplied string (e.g. app name and
    /// a resume command) appended to the exception message; omit it to get the generic message.
    /// </summary>
    public static async ValueTask<ProcessLock> AcquireAsync(string sessionDir, CancellationToken ct, string? hint = null)
    {
        var path = Path.Combine(sessionDir, ".lock");
        // File sharing is advisory on Unix, so opening a held lock succeeds there.
        if (File.Exists(path))
        {
            var (holder, heldSince) = await ReadLockAsync(path, ct);
            if (holder > 0 && IsProcessAlive(holder))
                throw new ProcessLockHeldException(holder, heldSince, hint);
        }

        // Attempt 1: try to open/create exclusively
        try
        {
            var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            var bytes = System.Text.Encoding.UTF8.GetBytes($"{Environment.ProcessId}\n{DateTimeOffset.UtcNow:O}\n");
            fs.SetLength(0);
            await fs.WriteAsync(bytes, ct);
            await fs.FlushAsync(ct);
            return new ProcessLock(fs, path);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Attempt 2: check if the lock holder is alive by reading the existing lock file
            var (pid, startedAt) = await ReadLockAsync(path, ct);
            if (pid > 0 && IsProcessAlive(pid))
                throw new ProcessLockHeldException(pid, startedAt, hint);
            // Stale lock — delete and retry once
            try { File.Delete(path); } catch { }
        }

        // Attempt 3: retry after deleting stale lock
        try
        {
            var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            var bytes = System.Text.Encoding.UTF8.GetBytes($"{Environment.ProcessId}\n{DateTimeOffset.UtcNow:O}\n");
            fs.SetLength(0);
            await fs.WriteAsync(bytes, ct);
            await fs.FlushAsync(ct);
            return new ProcessLock(fs, path);
        }
        catch (IOException)
        {
            throw new ProcessLockHeldException(-1, "unknown", hint);
        }
    }

    private static async ValueTask<(int pid, string startedAt)> ReadLockAsync(string path, CancellationToken ct)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            var pidLine = await sr.ReadLineAsync(ct);
            var tsLine = await sr.ReadLineAsync(ct);
            if (int.TryParse(pidLine, out var pid)) return (pid, tsLine ?? "unknown");
        }
        catch { }
        return (-1, "unknown");
    }

    private static bool IsProcessAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        try { File.Delete(_path); } catch { }
    }
}
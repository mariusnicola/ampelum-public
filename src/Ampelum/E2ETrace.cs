using System.Runtime.CompilerServices;

namespace SignalGrid.Ampelum;

/// <summary>Writes build/CI-layer trace messages to the log file specified by
/// the <c>E2E_TRACE_LOG</c> environment variable, or to
/// <c>$TMP/ampelum-e2e-trace.log</c>. Used from AmpelumCachingChatClient and
/// FileDistributedCache to surface hydration, read, write, and hit/miss events
/// that would otherwise be suppressed by the dotnet test runner.</summary>
public static class E2ETrace
{
    private static readonly string LogPath = ResolveLogPath();

    /// <summary>Append a timestamped trace line to the E2E trace log.</summary>
    public static void Log(string message,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "")
    {
        var type = Path.GetFileNameWithoutExtension(file);
        var ts = DateTimeOffset.UtcNow.ToString("O");
        var line = $"{ts} [{type}.{member}] {message}";
        using var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var writer = new StreamWriter(stream);
        writer.WriteLine(line);
    }

    private static string ResolveLogPath()
    {
        var env = Environment.GetEnvironmentVariable("E2E_TRACE_LOG");
        if (!string.IsNullOrWhiteSpace(env))
            return env;
        return Path.Combine(Path.GetTempPath(), "ampelum-e2e-trace.log");
    }
}

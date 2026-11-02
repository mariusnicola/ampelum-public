using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Ampelum.ComparativeBenchmarks.SubprocessHarness;

/// <summary>
/// Wrapper for invoking the Python reference implementation as a subprocess
/// (alpha.5 wires the body — the v0.9.1-alpha.4 cut shipped only the contract
/// surface).
///
/// <para>Discovery:</para>
/// <see cref="IsAvailable"/> probes <c>uv</c> first (preferred — the python-ref
/// scaffolding ships a <c>pyproject.toml</c> + <c>uv.lock</c>), then falls back
/// to <c>python</c>/<c>python3</c> on PATH. If neither is found, callers must
/// short-circuit (BenchmarkDotNet has no <c>[Skip]</c> attribute — scenarios
/// detect availability in <see cref="BenchmarkDotNet.Attributes.GlobalSetupAttribute"/>
/// and either skip the row or yield a sentinel result).
///
/// <para>Wire protocol:</para>
/// <list type="bullet">
///   <item>Send: a single JSON line on stdin with shape
///   <c>{"scenario","examples","mock_responses","seed"}</c>.</item>
///   <item>Receive: a single JSON line on stdout with shape
///   <c>{"elapsed_ns","peak_working_set_bytes","prompt_tokens",
///   "completion_tokens","output"}</c>.</item>
/// </list>
///
/// <para>Environment variables (override defaults):</para>
/// <list type="bullet">
///   <item><c>AMPELUM_COMPARATIVE_PY</c> — absolute path to a Python interpreter.
///   When set, <see cref="IsAvailable"/> trusts it without probing PATH.</item>
///   <item><c>AMPELUM_COMPARATIVE_PY_DIR</c> — directory containing
///   <c>runner.py</c>. Default: <c>tests/Ampelum.ComparativeBenchmarks/python-ref</c>
///   resolved relative to <see cref="AppContext.BaseDirectory"/>.</item>
/// </list>
/// </summary>
public sealed class PythonDspyRunner
{
    /// <summary>Scenario identifier passed verbatim to the Python reference script.</summary>
    public required string ScenarioId { get; init; }

    /// <summary>Random seed forwarded to the Python reference script.</summary>
    public int Seed { get; init; }

    /// <summary>Number of examples for optimizer scenarios (S8). Ignored elsewhere.</summary>
    public int Examples { get; init; }

    /// <summary>Mock LM responses cycled by the Python script in scenarios that need a deterministic LM.</summary>
    public IReadOnlyList<string> MockResponses { get; init; } = [];

    /// <summary>
    /// Cached availability probe. Returns <c>true</c> if a Python launcher
    /// (uv > python > python3) was found on PATH or <c>AMPELUM_COMPARATIVE_PY</c>
    /// was set; <c>false</c> otherwise. The probe runs at most once per process.
    /// </summary>
    public static bool IsAvailable => _isAvailable.Value;
    private static readonly Lazy<bool> _isAvailable = new(ProbeAvailability);

    /// <summary>
    /// Returns the launcher tuple <c>(filename, argsPrefix)</c> to spawn the
    /// Python script. Prefers <c>uv run python</c> when uv is on PATH so the
    /// pinned <c>uv.lock</c> is honoured; otherwise falls back to bare
    /// <c>python</c>/<c>python3</c>.
    /// </summary>
    public static (string FileName, string[] ArgsPrefix)? Launcher => _launcher.Value;
    private static readonly Lazy<(string, string[])?> _launcher = new(ProbeLauncher);

    /// <summary>Runs the Python reference for the configured scenario.</summary>
    /// <returns>The parsed <see cref="RunResult"/> from the subprocess stdout.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no Python launcher is available. Callers should check
    /// <see cref="IsAvailable"/> first and short-circuit the row when false —
    /// BenchmarkDotNet does not support a <c>[Skip]</c> attribute.
    /// </exception>
    public async Task<RunResult> RunAsync(CancellationToken ct = default)
    {
        if (!IsAvailable || Launcher is not { } launcher)
            throw new InvalidOperationException(
                "Python reference is not available on this machine. " +
                "Install uv (https://docs.astral.sh/uv/) or set " +
                "AMPELUM_COMPARATIVE_PY to a Python 3.12+ interpreter.");

        var scriptDir = ResolveScriptDir();
        var scriptPath = Path.Combine(scriptDir, "runner.py");
        if (!File.Exists(scriptPath))
            throw new InvalidOperationException(
                $"Python reference script not found at '{scriptPath}'. " +
                "See tests/Ampelum.ComparativeBenchmarks/python-ref/README for setup.");

        var psi = new ProcessStartInfo
        {
            FileName = launcher.FileName,
            WorkingDirectory = scriptDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in launcher.ArgsPrefix) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(scriptPath);

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var request = new
        {
            scenario = ScenarioId,
            examples = Examples,
            mock_responses = MockResponses,
            seed = Seed,
        };
        var requestJson = JsonSerializer.Serialize(request);
        await proc.StandardInput.WriteLineAsync(requestJson.AsMemory(), ct).ConfigureAwait(false);
        await proc.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        proc.StandardInput.Close();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"Python reference exited with code {proc.ExitCode}. Stderr: {stderr}");

        // The script may print warnings/log lines on stdout before the JSON line.
        // Take the LAST non-empty line and parse it.
        string? lastLine = null;
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!string.IsNullOrEmpty(line)) lastLine = line;

        if (lastLine is null)
            throw new InvalidOperationException(
                $"Python reference produced no stdout. Stderr: {stderr}");

        return JsonSerializer.Deserialize<RunResult>(lastLine, _replyJson)
            ?? throw new InvalidOperationException(
                $"Failed to deserialise Python reply: '{lastLine}'");
    }

    private static readonly JsonSerializerOptions _replyJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static string ResolveScriptDir()
    {
        var env = Environment.GetEnvironmentVariable("AMPELUM_COMPARATIVE_PY_DIR");
        if (!string.IsNullOrEmpty(env)) return env;

        // Walk up from BaseDirectory looking for the python-ref folder so the
        // benchmark binary can find it whether it's run from the repo root or
        // from the bin output directory.
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, "tests", "Ampelum.ComparativeBenchmarks", "python-ref");
            if (Directory.Exists(candidate)) return candidate;

            var sibling = Path.Combine(dir, "python-ref");
            if (Directory.Exists(sibling)) return sibling;
        }

        // Fall back to the conventional path; RunAsync will surface a clean error
        // if the directory does not exist.
        return Path.Combine(AppContext.BaseDirectory, "python-ref");
    }

    private static bool ProbeAvailability() => ProbeLauncher() is not null;

    private static (string FileName, string[] ArgsPrefix)? ProbeLauncher()
    {
        var explicitPy = Environment.GetEnvironmentVariable("AMPELUM_COMPARATIVE_PY");
        if (!string.IsNullOrEmpty(explicitPy) && File.Exists(explicitPy))
            return (explicitPy, []);

        // Prefer uv (pinned environment via uv.lock).
        if (TryRunVersion("uv")) return ("uv", ["run", "--quiet", "python"]);
        if (TryRunVersion("python")) return ("python", []);
        if (TryRunVersion("python3")) return ("python3", []);
        return null;
    }

    private static bool TryRunVersion(string exe)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                ArgumentList = { "--version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return false;
            return p.WaitForExit(2000) && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Outcome of a single Python subprocess invocation.</summary>
public sealed record RunResult(
    long ElapsedNs,
    long PeakWorkingSetBytes,
    long PromptTokens,
    long CompletionTokens,
    string Output);

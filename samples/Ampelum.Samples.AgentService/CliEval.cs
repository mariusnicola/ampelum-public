using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Ampelum.Samples.AgentService;

public sealed record EvalRow(string Input, string Expected, string Predicted);

/// <summary>
/// Runs the <c>ampelum eval</c> command as a process, the only way to reach it because the CLI keeps its
/// commands internal. The command scores predictions already in the dataset against the expected
/// values; it never calls a model.
/// </summary>
public static class CliEval
{
    public static string CliAssemblyPath => Path.Combine(AppContext.BaseDirectory, "SignalGrid.Ampelum.Cli.dll");

    public static async Task WriteDatasetAsync(string path, IEnumerable<EvalRow> rows, CancellationToken ct = default)
    {
        var lines = rows.Select(r => JsonSerializer.Serialize(new { input = r.Input, expected = r.Expected, predicted = r.Predicted }));
        await File.WriteAllLinesAsync(path, lines, ct);
    }

    /// <summary>How long the CLI may run before it is stopped.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Runs the CLI against a dataset and returns its exit code and output. When <paramref name="timeout"/>
    /// passes or <paramref name="ct"/> is cancelled, the process started here is killed with its children.
    /// </summary>
    /// <exception cref="TimeoutException">The CLI did not finish within <paramref name="timeout"/>.</exception>
    public static async Task<(int ExitCode, string Output)> RunAsync(
        string dataset, string metric, string outputDir, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo(DotnetHostPath())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[]
                 {
                     CliAssemblyPath, "eval",
                     "--signature", typeof(AnswerSignature).FullName!,
                     "--dataset", dataset,
                     "--metric", metric,
                     "--output", outputDir,
                 })
            start.ArgumentList.Add(arg);

        var limit = timeout ?? DefaultTimeout;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The CLI process did not start.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(limit);
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            if (ct.IsCancellationRequested)
                throw;
            throw new TimeoutException($"The CLI did not finish within {limit.TotalSeconds:0.###} seconds and was stopped.");
        }
        return (process.ExitCode, await stdout + await stderr);
    }

    /// <summary>The dotnet executable that runs this process, found from the runtime directory so PATH is not consulted.</summary>
    static string DotnetHostPath()
    {
        var runtimeDirectory = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var root = runtimeDirectory.Parent?.Parent?.Parent
                   ?? throw new InvalidOperationException("Cannot locate the dotnet root from the runtime directory.");
        var executable = Path.Combine(root.FullName, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return File.Exists(executable)
            ? executable
            : throw new FileNotFoundException("The dotnet executable was not found next to the runtime.", executable);
    }
}

using System.CommandLine;

namespace Ampelum.Cli.Tests;

internal static class StdoutCapture
{
    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        CommandLineConfiguration config,
        string commandLine,
        CancellationToken ct)
    {
        await using var outWriter = new StringWriter();
        await using var errWriter = new StringWriter();
        config.Output = outWriter;
        config.Error = errWriter;
        var exit = await config.InvokeAsync(commandLine, ct).ConfigureAwait(false);
        return (exit, outWriter.ToString(), errWriter.ToString());
    }
}

using System.Diagnostics;

namespace SignalGrid.Ampelum.Cli;

/// <summary>Default <see cref="IProcessRunner"/> using <see cref="Process"/>.</summary>
internal sealed class DefaultProcessRunner : IProcessRunner
{
    public async Task<int> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName) { UseShellExecute = false };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process: {fileName}");
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return proc.ExitCode;
    }
}

namespace SignalGrid.Ampelum.Cli;

/// <summary>Abstracts process invocation so commands can be unit-tested.</summary>
internal interface IProcessRunner
{
    /// <summary>Runs <paramref name="fileName"/> with <paramref name="args"/>; returns the exit code.</summary>
    Task<int> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken ct);
}

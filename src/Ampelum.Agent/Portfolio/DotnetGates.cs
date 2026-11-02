using System.Diagnostics;

namespace SignalGrid.Ampelum.Agent;

/// <summary>Quality gate that runs `dotnet build` on a project.</summary>
public sealed class DotnetBuildGate : IQualityGate
{
    public string Id => "build";
    public string DisplayName => ".NET Build";

    public async Task<QualityGateResult> RunAsync(string workspaceRoot, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build \"{workspaceRoot}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null)
                return Fail("Failed to start dotnet build process", sw);

            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            sw.Stop();

            return new QualityGateResult(
                GateId: Id,
                Success: process.ExitCode == 0,
                Output: process.ExitCode == 0 ? "Build succeeded" : stderr,
                FindingsCount: null,
                Duration: sw.Elapsed,
                Error: process.ExitCode == 0 ? null : stderr
            );
        }
        catch (Exception ex)
        {
            return Fail(ex.Message, sw);
        }
    }

    private QualityGateResult Fail(string error, Stopwatch sw)
    {
        sw.Stop();
        return new QualityGateResult(Id, false, null, null, sw.Elapsed, error);
    }
}

/// <summary>Quality gate that runs `dotnet test` on a project.</summary>
public sealed class DotnetTestGate : IQualityGate
{
    public string Id => "test";
    public string DisplayName => ".NET Test";

    public async Task<QualityGateResult> RunAsync(string workspaceRoot, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"test \"{workspaceRoot}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null)
                return Fail("Failed to start dotnet test process", sw);

            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            sw.Stop();

            // Count passed/failed from output
            var passedMatch = System.Text.RegularExpressions.Regex.Match(stdout, @"Passed!\s+-\s+Failed:\s+(\d+)");
            var failedCount = passedMatch.Success ? int.Parse(passedMatch.Groups[1].Value) : (process.ExitCode == 0 ? 0 : 1);

            return new QualityGateResult(
                GateId: Id,
                Success: process.ExitCode == 0,
                Output: process.ExitCode == 0 ? "All tests passed" : stdout,
                FindingsCount: failedCount,
                Duration: sw.Elapsed,
                Error: process.ExitCode == 0 ? null : stderr
            );
        }
        catch (Exception ex)
        {
            return Fail(ex.Message, sw);
        }
    }

    private QualityGateResult Fail(string error, Stopwatch sw)
    {
        sw.Stop();
        return new QualityGateResult(Id, false, null, null, sw.Elapsed, error);
    }
}

/// <summary>Quality gate that runs `dotnet format --verify-no-changes`.</summary>
public sealed class DotnetLintGate : IQualityGate
{
    public string Id => "lint";
    public string DisplayName => ".NET Lint";

    public async Task<QualityGateResult> RunAsync(string workspaceRoot, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"format \"{workspaceRoot}\" --verify-no-changes",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null)
                return Fail("Failed to start dotnet format process", sw);

            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            sw.Stop();

            return new QualityGateResult(
                GateId: Id,
                Success: process.ExitCode == 0,
                Output: process.ExitCode == 0 ? "No formatting issues" : stdout,
                FindingsCount: null,
                Duration: sw.Elapsed,
                Error: process.ExitCode == 0 ? null : stderr
            );
        }
        catch (Exception ex)
        {
            return Fail(ex.Message, sw);
        }
    }

    private QualityGateResult Fail(string error, Stopwatch sw)
    {
        sw.Stop();
        return new QualityGateResult(Id, false, null, null, sw.Elapsed, error);
    }
}

/// <summary>Registers the default gates for the software domain.</summary>
public static class DefaultGates
{
    /// <summary>The standard gates: build, test, lint, security.</summary>
    public static IReadOnlyList<IQualityGate> SoftwareGates { get; } = new IQualityGate[]
    {
        new DotnetBuildGate(),
        new DotnetTestGate(),
        new DotnetLintGate(),
    };
}
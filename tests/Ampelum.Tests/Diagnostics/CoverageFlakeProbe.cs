using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace Ampelum.Tests.FlakeProbe;

/// <summary>
/// Probe harness for the v0.6c-flagged "XPlat Code Coverage flakes ~357 unrelated tests"
/// issue. Committed for reproducibility. Skipped unless the environment variable
/// <c>AMPELUM_FLAKE_PROBE=1</c> is set, so it is invisible in normal CI runs.
///
/// To execute:
/// <code>
/// AMPELUM_FLAKE_PROBE=1 dotnet test tests/SignalGrid.Ampelum.Tests/Ampelum.Tests.csproj \
///   -c Release --filter "Category=Probe"
/// </code>
///
/// The probe runs the gated test suite N=10 times under XPlat Code Coverage and writes
/// per-iteration failure counts to <c>TestResults/flake-probe-report.txt</c> next to
/// the trx output. Failures are NOT thrown back to xUnit â€” every iteration runs even
/// if a previous one crashed the test host. The output file is the artifact.
/// </summary>
public sealed partial class CoverageFlakeProbe
{
    [Fact]
    [Trait("Category", "Probe")]
    public async Task ProbeRunsTenIterationsAndDumpsFlakes()
    {
        if (Environment.GetEnvironmentVariable("AMPELUM_FLAKE_PROBE") != "1")
        {
            return;
        }

        var report = new List<string>();
        for (int i = 0; i < 10; i++)
        {
            var sw = Stopwatch.StartNew();
            var psi = new ProcessStartInfo("dotnet")
            {
                ArgumentList =
                {
                    "test",
                    "tests/SignalGrid.Ampelum.Tests/Ampelum.Tests.csproj",
                    "-c", "Release", "--no-build",
                    "--collect:XPlat Code Coverage",
                    "--logger:trx",
                    "--results-directory", $"TestResults/probe-{i}",
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi)!;
            var stdout = await p.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            await p.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await p.WaitForExitAsync(TestContext.Current.CancellationToken);
            sw.Stop();

            var failedTests = ExtractFailedTestNames(stdout);
            report.Add($"iter {i}: exit={p.ExitCode} elapsed={sw.Elapsed.TotalSeconds:F1}s failures={failedTests.Count}");
            foreach (var t in failedTests)
            {
                report.Add($"  - {t}");
            }
        }

        Directory.CreateDirectory("TestResults");
        await File.WriteAllLinesAsync("TestResults/flake-probe-report.txt", report, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Parses the xunit.v3 console reporter for "Failed" lines. The reporter emits
    /// lines of the form <c>      Failed FullyQualifiedTestName [N ms]</c>.
    /// </summary>
    internal static List<string> ExtractFailedTestNames(string stdout)
    {
        var failures = new List<string>();
        foreach (Match m in FailedLineRegex().Matches(stdout))
        {
            failures.Add(m.Groups["name"].Value);
        }
        return failures;
    }

    [GeneratedRegex(@"^\s+Failed\s+(?<name>[^\s\[]+)", RegexOptions.Multiline)]
    private static partial Regex FailedLineRegex();
}

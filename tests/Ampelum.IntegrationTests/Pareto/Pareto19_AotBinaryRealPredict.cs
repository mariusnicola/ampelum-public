using System.Diagnostics;
using SignalGrid.Ampelum;
using Ampelum.IntegrationTests.Skips;
using Shouldly;
using Xunit;

namespace Ampelum.IntegrationTests.Pareto;

/// <summary>
/// Pareto integration test #19 (master plan Â§6.H, v0.9.1-alpha.3). AOT smoke through
/// Predict: the AOT-published <c>tests/SignalGrid.Ampelum.AotSmoke</c> binary executes a single
/// <see cref="Predict{TSig}.ForwardAsync"/> against a fake <c>IChatClient</c> and
/// emits a stable, parse-friendly stdout contract:
/// <list type="bullet">
///   <item><c>Predict completed: &lt;answer&gt;</c></item>
///   <item><c>SignalGrid.Ampelum.trace.id=&lt;hex&gt;</c> (proves the OpenTelemetry
///         <see cref="System.Diagnostics.ActivitySource"/> emits in AOT)</item>
///   <item><c>aot-smoke ok</c></item>
/// </list>
/// The probe binary path is supplied via <c>AMPELUM_AOT_PROBE_PATH</c>; CI publishes
/// the AotSmoke project to a known location and exports that env var.
/// </summary>
/// <remarks>
/// Skips when <c>AMPELUM_AOT_PROBE_PATH</c> is unset (default local dev). The user
/// task said "AOT smoke through Predict" + "may need <c>tests/SignalGrid.Ampelum.AotSmoke</c>
/// extension rather than IntegrationTests" â€” this test is the IntegrationTests-side
/// driver for the published binary; the binary itself lives in
/// <c>tests/SignalGrid.Ampelum.AotSmoke/Program.cs</c> and was extended in this commit to emit
/// the parse-friendly contract above.
/// </remarks>
public sealed class Pareto19_AotBinaryRealPredict
{
    /// <summary>AOT-published binary completes a real Predict, emits trace id, returns 0.</summary>
    [Fact]
    [Trait("Cost", "Cheap")]
    public async Task AotPublishedBinary_ForwardCompletes_EmitsTraceId_NoJitFallback()
    {
        Assert.SkipWhen(SkipUnlessAotPublished.ShouldSkip, SkipUnlessAotPublished.Reason);
        var ct = TestContext.Current.CancellationToken;

        var probePath = Environment.GetEnvironmentVariable(SkipUnlessAotPublished.EnvVarName)!;
        File.Exists(probePath).ShouldBeTrue($"AOT probe binary not found at '{probePath}'");

        var psi = new ProcessStartInfo(probePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        proc.ExitCode.ShouldBe(0, $"stderr: {stderr}");
        stdout.ShouldContain("Predict completed: Paris");
        stdout.ShouldContain("SignalGrid.Ampelum.trace.id=");
        stdout.ShouldContain("aot-smoke ok");

        // No JIT-fallback warnings should leak into stderr â€” TreatWarningsAsErrors
        // catches them at publish time, but assert at runtime as belt-and-braces.
        stderr.ShouldNotContain("RequiresDynamicCode", Case.Insensitive);
        stderr.ShouldNotContain("RequiresUnreferencedCode", Case.Insensitive);

        // Trace id is non-empty hex (32 chars in W3C format). Tolerate older formats
        // by asserting only "non-empty after the equals sign".
        var traceLine = stdout.Split('\n').Single(line => line.Contains("SignalGrid.Ampelum.trace.id="));
        var traceId = traceLine.Substring(traceLine.IndexOf('=') + 1).Trim();
        traceId.ShouldNotBeNullOrWhiteSpace();
        traceId.ShouldNotBe("00000000000000000000000000000000");
    }
}

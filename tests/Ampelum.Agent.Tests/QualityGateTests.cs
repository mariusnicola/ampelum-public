using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

public sealed class QualityGateTests
{
    // ── IQualityGate interface contract ───────────────────────────

    [Fact]
    public void DotnetBuildGate_HasCorrectId()
    {
        var gate = new DotnetBuildGate();
        gate.Id.ShouldBe("build");
        gate.DisplayName.ShouldBe(".NET Build");
    }

    [Fact]
    public void DotnetTestGate_HasCorrectId()
    {
        var gate = new DotnetTestGate();
        gate.Id.ShouldBe("test");
        gate.DisplayName.ShouldBe(".NET Test");
    }

    [Fact]
    public void DotnetLintGate_HasCorrectId()
    {
        var gate = new DotnetLintGate();
        gate.Id.ShouldBe("lint");
        gate.DisplayName.ShouldBe(".NET Lint");
    }

    // ── DefaultGates ─────────────────────────────────────────────

    [Fact]
    public void DefaultGates_HasAllThree()
    {
        DefaultGates.SoftwareGates.Count.ShouldBe(3);
        DefaultGates.SoftwareGates.ShouldContain(g => g.Id == "build");
        DefaultGates.SoftwareGates.ShouldContain(g => g.Id == "test");
        DefaultGates.SoftwareGates.ShouldContain(g => g.Id == "lint");
    }

    // ── QualityGateResult ────────────────────────────────────────

    [Fact]
    public void QualityGateResult_Success()
    {
        var result = new QualityGateResult("build", true, "Build OK", null, TimeSpan.FromSeconds(1));
        result.GateId.ShouldBe("build");
        result.Success.ShouldBeTrue();
        result.Output.ShouldBe("Build OK");
        result.FindingsCount.ShouldBeNull();
    }

    [Fact]
    public void QualityGateResult_FailureWithFindings()
    {
        var result = new QualityGateResult("test", false, "Tests failed", 3, TimeSpan.FromSeconds(5), "3 failures");
        result.Success.ShouldBeFalse();
        result.FindingsCount.ShouldBe(3);
        result.Error.ShouldBe("3 failures");
    }

    // ── RunAsync on nonexistent project returns failure ───────────

    [Fact]
    public async Task DotnetBuildGate_NonexistentProject_ReturnsFailure()
    {
        var gate = new DotnetBuildGate();
        var result = await gate.RunAsync(@"C:\nonexistent\project.csproj");

        result.Success.ShouldBeFalse();
        result.GateId.ShouldBe("build");
        result.Duration.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    // ── IDomainProfile.Gates default ─────────────────────────────

    [Fact]
    public void DomainProfile_Gates_DefaultIsNull()
    {
        // Default interface method requires calling through the interface
        IDomainProfile profile = new SoftwareDomainProfile();
        profile.Gates.ShouldBeNull();
    }
}
using System.Text.Json;
using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tools.Tests;

public sealed class SandboxContractTests
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void SandboxCommand_RoundTrips_Through_Json()
    {
        var command = new SandboxCommand
        {
            Kind = SandboxCommandKind.Process,
            Program = "dotnet",
            Arguments = ["--info", "--verbosity", "minimal"],
            WorkingDirectory = @"C:\\work",
            StandardInput = "hello",
            CorrelationId = "run-123",
            Policy = new SandboxPolicy
            {
                IsolationMode = SandboxIsolationMode.Process,
                Timeout = TimeSpan.FromSeconds(30),
                AllowFileWrites = true,
                PreserveEnvironment = false,
                Network = new SandboxNetworkPolicy
                {
                    Access = SandboxNetworkAccess.DenyAll,
                    AllowedHosts = ["localhost"],
                    AllowedPorts = [80, 443],
                },
                AllowedWorkingDirectories = [@"C:\\work"],
            },
        };

        var json = JsonSerializer.Serialize(command, JsonOpts);
        var roundtrip = JsonSerializer.Deserialize<SandboxCommand>(json, JsonOpts);

        roundtrip.ShouldNotBeNull();
        roundtrip.Kind.ShouldBe(SandboxCommandKind.Process);
        roundtrip.Program.ShouldBe("dotnet");
        roundtrip.Arguments.ShouldBe(["--info", "--verbosity", "minimal"]);
        roundtrip.WorkingDirectory.ShouldBe(@"C:\\work");
        roundtrip.StandardInput.ShouldBe("hello");
        roundtrip.CorrelationId.ShouldBe("run-123");
        roundtrip.Policy.Timeout.ShouldBe(TimeSpan.FromSeconds(30));
        roundtrip.Policy.Network.Access.ShouldBe(SandboxNetworkAccess.DenyAll);
    }

    [Fact]
    public void SandboxPolicy_RoundTrips_Through_Json()
    {
        var policy = new SandboxPolicy
        {
            IsolationMode = SandboxIsolationMode.Container,
            Timeout = TimeSpan.FromMinutes(2),
            AllowFileWrites = false,
            PreserveEnvironment = true,
            Network = new SandboxNetworkPolicy
            {
                Access = SandboxNetworkAccess.AllowList,
                AllowedHosts = ["example.com"],
                AllowedPorts = [8080],
            },
            AllowedWorkingDirectories = [@"C:\\repo"],
        };

        var json = JsonSerializer.Serialize(policy, JsonOpts);
        var roundtrip = JsonSerializer.Deserialize<SandboxPolicy>(json, JsonOpts);

        roundtrip.ShouldNotBeNull();
        roundtrip.IsolationMode.ShouldBe(SandboxIsolationMode.Container);
        roundtrip.Timeout.ShouldBe(TimeSpan.FromMinutes(2));
        roundtrip.PreserveEnvironment.ShouldBeTrue();
        roundtrip.Network.Access.ShouldBe(SandboxNetworkAccess.AllowList);
        roundtrip.Network.AllowedHosts.ShouldBe(["example.com"]);
    }

    [Fact]
    public void SandboxResult_Reports_Status_And_Metadata()
    {
        var result = new SandboxResult
        {
            Outcome = SandboxOutcome.TimedOut,
            ExitCode = -1,
            StandardOutput = "out",
            StandardError = "err",
            Duration = TimeSpan.FromSeconds(5),
            RejectionReason = "Command timed out after 5s",
            CorrelationId = "run-456",
        };

        result.Outcome.ShouldBe(SandboxOutcome.TimedOut);
        result.ExitCode.ShouldBe(-1);
        result.StandardOutput.ShouldBe("out");
        result.StandardError.ShouldBe("err");
        result.Duration.ShouldBe(TimeSpan.FromSeconds(5));
        result.RejectionReason.ShouldBe("Command timed out after 5s");
        result.CorrelationId.ShouldBe("run-456");
    }

    [Fact]
    public void SandboxPolicy_RequiresIsolation_Is_False_For_Default_Unspecified_Policy()
    {
        var policy = new SandboxPolicy();

        policy.RequiresIsolation.ShouldBeFalse();
    }

    [Fact]
    public void SandboxPolicy_RequiresIsolation_Is_False_For_Fully_Trusted_Policy()
    {
        var policy = new SandboxPolicy
        {
            AllowFileWrites = true,
            Network = new SandboxNetworkPolicy { Access = SandboxNetworkAccess.AllowAll },
        };

        policy.RequiresIsolation.ShouldBeFalse();
    }

    [Theory]
    [InlineData(SandboxNetworkAccess.DenyAll)]
    [InlineData(SandboxNetworkAccess.LoopbackOnly)]
    [InlineData(SandboxNetworkAccess.AllowList)]
    public void SandboxPolicy_RequiresIsolation_Is_True_When_Network_Is_Restricted(SandboxNetworkAccess access)
    {
        var policy = new SandboxPolicy
        {
            AllowFileWrites = true,
            Network = new SandboxNetworkPolicy { Access = access },
        };

        policy.RequiresIsolation.ShouldBeTrue();
    }

    [Fact]
    public void SandboxPolicy_RequiresIsolation_Is_True_When_File_Writes_Are_Restricted()
    {
        var policy = new SandboxPolicy
        {
            AllowFileWrites = false,
            Network = new SandboxNetworkPolicy { Access = SandboxNetworkAccess.AllowAll },
        };

        policy.RequiresIsolation.ShouldBeTrue();
    }
}

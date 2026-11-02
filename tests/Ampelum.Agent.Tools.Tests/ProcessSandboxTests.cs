using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tools.Tests;

public sealed class ProcessSandboxTests : IDisposable
{
    readonly string _workspaceRoot;

    public ProcessSandboxTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "ampelum-sandbox-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceRoot);
    }

    [Fact]
    public async Task ExecuteAsync_Runs_A_Command_And_Captures_Output()
    {
        var sandbox = new ProcessSandbox();
        var command = new SandboxCommand
        {
            Kind = SandboxCommandKind.Process,
            Program = "dotnet",
            Arguments = ["--version"],
            WorkingDirectory = _workspaceRoot,
            Policy = new SandboxPolicy
            {
                IsolationMode = SandboxIsolationMode.Process,
                Timeout = TimeSpan.FromSeconds(30),
                AllowFileWrites = true,
                PreserveEnvironment = false,
                Network = new SandboxNetworkPolicy
                {
                    Access = SandboxNetworkAccess.AllowAll,
                    AllowedHosts = Array.Empty<string>(),
                    AllowedPorts = Array.Empty<int>(),
                },
                AllowedWorkingDirectories = [_workspaceRoot],
            },
        };

        var result = await sandbox.ExecuteAsync(command, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(SandboxOutcome.Succeeded);
        result.ExitCode.ShouldBe(0);
        result.StandardOutput.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ExecuteAsync_Runs_A_Command_With_Default_Unspecified_Policy()
    {
        var sandbox = new ProcessSandbox();
        var command = new SandboxCommand
        {
            Kind = SandboxCommandKind.Process,
            Program = "dotnet",
            Arguments = ["--version"],
            WorkingDirectory = _workspaceRoot,
        };

        var result = await sandbox.ExecuteAsync(command, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(SandboxOutcome.Succeeded);
        result.ExitCode.ShouldBe(0);
        result.StandardOutput.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ExecuteAsync_Accepts_WorkingDirectory_Matching_Second_Configured_Root()
    {
        var sandbox = new ProcessSandbox();
        var secondRoot = Path.Combine(Path.GetTempPath(), "ampelum-sandbox-second-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(secondRoot);

        try
        {
            var command = new SandboxCommand
            {
                Kind = SandboxCommandKind.Process,
                Program = "dotnet",
                Arguments = ["--version"],
                WorkingDirectory = secondRoot,
                Policy = new SandboxPolicy
                {
                    IsolationMode = SandboxIsolationMode.Process,
                    Timeout = TimeSpan.FromSeconds(30),
                    AllowedWorkingDirectories = [_workspaceRoot, secondRoot],
                },
            };

            var result = await sandbox.ExecuteAsync(command, TestContext.Current.CancellationToken);

            result.Outcome.ShouldBe(SandboxOutcome.Succeeded);
        }
        finally
        {
            try { Directory.Delete(secondRoot, true); }
            catch { }
        }
    }

    [Fact]
    public async Task ExecuteAsync_Rejects_Sibling_Directory_With_Shared_Prefix()
    {
        var sandbox = new ProcessSandbox();
        var allowedRoot = Path.Combine(_workspaceRoot, "work");
        var siblingWithSharedPrefix = Path.Combine(_workspaceRoot, "work-other");
        Directory.CreateDirectory(allowedRoot);
        Directory.CreateDirectory(siblingWithSharedPrefix);

        var command = new SandboxCommand
        {
            Kind = SandboxCommandKind.Process,
            Program = "dotnet",
            Arguments = ["--version"],
            WorkingDirectory = siblingWithSharedPrefix,
            Policy = new SandboxPolicy
            {
                IsolationMode = SandboxIsolationMode.Process,
                Timeout = TimeSpan.FromSeconds(10),
                AllowedWorkingDirectories = [allowedRoot],
            },
        };

        var result = await sandbox.ExecuteAsync(command, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(SandboxOutcome.Rejected);
        var rejection = result.RejectionReason;
        rejection.ShouldNotBeNull();
        rejection.ShouldContain("Working directory");
    }

    [Fact]
    public async Task ExecuteAsync_Scrubs_Environment_When_PreserveEnvironment_Is_False()
    {
        var sandbox = new ProcessSandbox();
        const string MarkerVariable = "AMPELUM_SANDBOX_TEST_MARKER";
        Environment.SetEnvironmentVariable(MarkerVariable, "leak-me");

        try
        {
            var command = new SandboxCommand
            {
                Kind = SandboxCommandKind.Process,
                Program = "powershell",
                Arguments = ["-NoProfile", "-Command", $"[System.Environment]::GetEnvironmentVariable('{MarkerVariable}')"],
                WorkingDirectory = _workspaceRoot,
                Policy = new SandboxPolicy
                {
                    IsolationMode = SandboxIsolationMode.Process,
                    Timeout = TimeSpan.FromSeconds(10),
                    PreserveEnvironment = false,
                    AllowedWorkingDirectories = [_workspaceRoot],
                },
            };

            var result = await sandbox.ExecuteAsync(command, TestContext.Current.CancellationToken);

            result.Outcome.ShouldBe(SandboxOutcome.Succeeded);
            result.StandardOutput.ShouldNotContain("leak-me");
        }
        finally
        {
            Environment.SetEnvironmentVariable(MarkerVariable, null);
        }
    }

    [Fact]
    public async Task ExecuteAsync_Rejects_Network_Restricted_Command_Without_Starting_A_Process()
    {
        var sandbox = new ProcessSandbox();
        var markerFile = Path.Combine(_workspaceRoot, "network-marker.txt");
        var command = new SandboxCommand
        {
            Kind = SandboxCommandKind.Process,
            Program = "powershell",
            Arguments = ["-NoProfile", "-Command", $"Set-Content -Path '{markerFile}' -Value 'ran'"],
            WorkingDirectory = _workspaceRoot,
            Policy = new SandboxPolicy
            {
                IsolationMode = SandboxIsolationMode.Process,
                Timeout = TimeSpan.FromSeconds(10),
                AllowFileWrites = true,
                PreserveEnvironment = false,
                Network = new SandboxNetworkPolicy
                {
                    Access = SandboxNetworkAccess.DenyAll,
                    AllowedHosts = Array.Empty<string>(),
                    AllowedPorts = Array.Empty<int>(),
                },
                AllowedWorkingDirectories = [_workspaceRoot],
            },
        };

        var result = await sandbox.ExecuteAsync(command, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(SandboxOutcome.Rejected);
        result.RejectionReason.ShouldNotBeNull();
        result.RejectionReason.ShouldContain("isolation");
        File.Exists(markerFile).ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_Rejects_Write_Restricted_Command_Without_Starting_A_Process()
    {
        var sandbox = new ProcessSandbox();
        var markerFile = Path.Combine(_workspaceRoot, "write-marker.txt");
        var command = new SandboxCommand
        {
            Kind = SandboxCommandKind.Process,
            Program = "powershell",
            Arguments = ["-NoProfile", "-Command", $"Set-Content -Path '{markerFile}' -Value 'ran'"],
            WorkingDirectory = _workspaceRoot,
            Policy = new SandboxPolicy
            {
                IsolationMode = SandboxIsolationMode.Process,
                Timeout = TimeSpan.FromSeconds(10),
                AllowFileWrites = false,
                PreserveEnvironment = false,
                Network = new SandboxNetworkPolicy
                {
                    Access = SandboxNetworkAccess.AllowAll,
                    AllowedHosts = Array.Empty<string>(),
                    AllowedPorts = Array.Empty<int>(),
                },
                AllowedWorkingDirectories = [_workspaceRoot],
            },
        };

        var result = await sandbox.ExecuteAsync(command, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(SandboxOutcome.Rejected);
        result.RejectionReason.ShouldNotBeNull();
        result.RejectionReason.ShouldContain("isolation");
        File.Exists(markerFile).ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_Rejects_WorkingDirectory_Escape()
    {
        var sandbox = new ProcessSandbox();
        var outsideDir = Path.Combine(Path.GetTempPath(), "ampelum-outside-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outsideDir);

        try
        {
            var command = new SandboxCommand
            {
                Kind = SandboxCommandKind.Process,
                Program = "dotnet",
                Arguments = ["--version"],
                WorkingDirectory = outsideDir,
                Policy = new SandboxPolicy
                {
                    IsolationMode = SandboxIsolationMode.Process,
                    Timeout = TimeSpan.FromSeconds(10),
                    AllowFileWrites = false,
                    PreserveEnvironment = false,
                    Network = new SandboxNetworkPolicy
                    {
                        Access = SandboxNetworkAccess.DenyAll,
                        AllowedHosts = Array.Empty<string>(),
                        AllowedPorts = Array.Empty<int>(),
                    },
                    AllowedWorkingDirectories = [_workspaceRoot],
                },
            };

            var result = await sandbox.ExecuteAsync(command, TestContext.Current.CancellationToken);

            result.Outcome.ShouldBe(SandboxOutcome.Rejected);
            var rejection = result.RejectionReason;
            rejection.ShouldNotBeNull();
            rejection.ShouldContain("Working directory");
        }
        finally
        {
            try { Directory.Delete(outsideDir, true); }
            catch { }
        }
    }

    [Fact]
    public async Task ExecuteAsync_Can_Be_Cancelled()
    {
        var sandbox = new ProcessSandbox();
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var command = new SandboxCommand
        {
            Kind = SandboxCommandKind.Process,
            Program = "powershell",
            Arguments = ["-NoProfile", "-Command", "Start-Sleep -Seconds 5"],
            WorkingDirectory = _workspaceRoot,
            Policy = new SandboxPolicy
            {
                IsolationMode = SandboxIsolationMode.Process,
                Timeout = TimeSpan.FromSeconds(30),
                AllowFileWrites = true,
                PreserveEnvironment = false,
                Network = new SandboxNetworkPolicy
                {
                    Access = SandboxNetworkAccess.AllowAll,
                    AllowedHosts = Array.Empty<string>(),
                    AllowedPorts = Array.Empty<int>(),
                },
                AllowedWorkingDirectories = [_workspaceRoot],
            },
        };

        var result = await sandbox.ExecuteAsync(command, cts.Token);

        result.Outcome.ShouldBe(SandboxOutcome.Cancelled);
        var reason = result.RejectionReason;
        reason.ShouldNotBeNull();
        reason.ToLowerInvariant().ShouldContain("cancelled");
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceRoot, true); }
        catch { }
    }
}

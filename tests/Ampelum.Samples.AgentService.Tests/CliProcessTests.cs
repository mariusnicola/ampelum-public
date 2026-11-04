using System.Diagnostics;
using Shouldly;
using Xunit;

namespace Ampelum.Samples.AgentService.Tests;

/// <summary>The Ampelum CLI's <c>new</c> and <c>repl</c> commands and the Host's <c>rebuild-index</c>, each run as a process.</summary>
public sealed class CliProcessTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ampelum-agent-service-cli-" + Guid.NewGuid().ToString("N"));

    public CliProcessTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task New_WritesTheAgentTemplateIntoTheOutputFolder_WithTheProjectName()
    {
        var output = Path.Combine(_dir, "generated");

        var (exit, text) = await CliNew.RunAsync("Orders.Agent", output, Path.Combine(_dir, "engine-home"), Ct);

        exit.ShouldBe(0, text);
        var files = Directory.GetFiles(output, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(output, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        files.ShouldBe(["Agents/Critic.cs", "Agents/Researcher.cs", "Orders.Agent.csproj", "Program.cs", "README.md", "Tools/CalculatorTool.cs"]);
        var program = await File.ReadAllTextAsync(Path.Combine(output, "Program.cs"), Ct);
        (program + await File.ReadAllTextAsync(Path.Combine(output, "Agents", "Critic.cs"), Ct)).ShouldContain("Orders.Agent");
        (await File.ReadAllTextAsync(Path.Combine(output, "Orders.Agent.csproj"), Ct)).ShouldContain("<PackageReference Include=\"Ampelum\"");
        Directory.GetDirectories(output).Select(Path.GetFileName).ShouldNotContain(".template.config");
    }

    [Fact]
    public async Task New_WithATemplateItDoesNotKnow_ExitsWith2AndListsTheOnesItDoes()
    {
        var (exit, text) = await CliEval.RunProcessAsync(CliEval.CliCommand("new", "no-such-template"), CliEval.DefaultTimeout, Ct);

        exit.ShouldBe(2);
        text.ShouldContain("Unknown template 'no-such-template'");
        text.ShouldContain(CliNew.Template);
    }

    /// <summary>
    /// Makes a working directory whose <c>data/audit.jsonl</c> holds the audit of <paramref name="calls"/> real forward
    /// calls. That is where the Host command looks when no settings file names another place.
    /// </summary>
    async Task<string> WorkingDirectoryWithAuditAsync(string name, int calls)
    {
        var workingDirectory = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.Combine(workingDirectory, "data"));
        await using var service = new ServiceFixture();
        await service.InitializeAsync();
        for (var i = 0; i < calls; i++)
        {
            service.EnqueueAnswer($"Answer {i}.");
            await service.Client.ForwardAsync(AgentServiceHost.ModuleName, $"Question {i}?", Ct);
        }
        // The Host still holds the audit file open for writing, so it is read with sharing allowed.
        await using var source = new FileStream(
            Path.Combine(service.StorageDir, "audit.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await using var target = File.Create(Path.Combine(workingDirectory, "data", "audit.jsonl"));
        await source.CopyToAsync(target, Ct);
        return workingDirectory;
    }

    [Fact]
    public async Task HostRebuildIndex_IndexesTheAuditFileInTheWorkingDirectory()
    {
        var workingDirectory = await WorkingDirectoryWithAuditAsync("host-cli", calls: 2);

        var (exit, text) = await HostRebuildCli.RunAsync(workingDirectory, [], Ct);

        exit.ShouldBe(0, text);
        text.ShouldContain("Rebuild complete: 2 audit entries, 0 prompt versions, 0 modules");
        File.Exists(Path.Combine(workingDirectory, "data", "host.db")).ShouldBeTrue();
    }

    [Fact]
    public async Task HostRebuildIndex_ReadsTheFileGivenToConfig()
    {
        var workingDirectory = await WorkingDirectoryWithAuditAsync("host-cli-config", calls: 2);
        var elsewhere = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.Copy(Path.Combine(workingDirectory, "data", "audit.jsonl"), Path.Combine(elsewhere, "named-audit.jsonl"));
        var config = Path.Combine(elsewhere, "host.json");
        await HostRebuildCli.WriteConfigAsync(
            config, Path.Combine(elsewhere, "named.db"), Path.Combine(elsewhere, "named-audit.jsonl"), Path.Combine(elsewhere, "prompts"), Ct);

        var (exit, text) = await HostRebuildCli.RunAsync(workingDirectory, ["--config", config], Ct);

        // The index lands where the named file says, not in ./data of the working directory.
        exit.ShouldBe(0, text);
        text.ShouldContain("Rebuild complete: 2 audit entries");
        File.Exists(Path.Combine(elsewhere, "named.db")).ShouldBeTrue();
        File.Exists(Path.Combine(workingDirectory, "data", "host.db")).ShouldBeFalse();
    }

    [Fact]
    public async Task HostRebuildIndex_ReadsARelativeConfigPathFromTheWorkingDirectory()
    {
        var workingDirectory = await WorkingDirectoryWithAuditAsync("host-cli-config-relative", calls: 1);
        await HostRebuildCli.WriteConfigAsync(
            Path.Combine(workingDirectory, "custom.json"),
            Path.Combine(workingDirectory, "custom.db"), Path.Combine(workingDirectory, "data", "audit.jsonl"),
            Path.Combine(workingDirectory, "prompts"), Ct);

        var (exit, text) = await HostRebuildCli.RunAsync(workingDirectory, ["--config", "custom.json"], Ct);

        exit.ShouldBe(0, text);
        text.ShouldContain("Rebuild complete: 1 audit entries");
        File.Exists(Path.Combine(workingDirectory, "custom.db")).ShouldBeTrue();
    }

    [Fact]
    public async Task HostRebuildIndex_WithAConfigFileThatDoesNotExist_SaysSoAndExitsNonZero()
    {
        var workingDirectory = await WorkingDirectoryWithAuditAsync("host-cli-config-missing", calls: 1);

        var (exit, text) = await HostRebuildCli.RunAsync(workingDirectory, ["--config", "missing.json"], Ct);

        exit.ShouldNotBe(0);
        text.ShouldContain($"Rebuild failed: config file '{Path.Combine(workingDirectory, "missing.json")}' was not found.");
        File.Exists(Path.Combine(workingDirectory, "data", "host.db")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("--config=custom.json")]
    [InlineData("-c=custom.json")]
    [InlineData("--confg")]
    public async Task HostRebuildIndex_WithAnArgumentItDoesNotKnow_NamesItAndExitsNonZero(string argument)
    {
        var workingDirectory = await WorkingDirectoryWithAuditAsync("host-cli-unknown-" + argument.Length, calls: 1);
        await HostRebuildCli.WriteConfigAsync(
            Path.Combine(workingDirectory, "custom.json"),
            Path.Combine(workingDirectory, "custom.db"), Path.Combine(workingDirectory, "data", "audit.jsonl"),
            Path.Combine(workingDirectory, "prompts"), Ct);

        var (exit, text) = await HostRebuildCli.RunAsync(workingDirectory, [argument], Ct);

        exit.ShouldNotBe(0);
        text.ShouldContain($"Rebuild failed: unknown argument '{argument}'. Use --config <path>.");
        // Nothing was indexed, neither into the file the argument named nor into the default location.
        File.Exists(Path.Combine(workingDirectory, "custom.db")).ShouldBeFalse();
        File.Exists(Path.Combine(workingDirectory, "data", "host.db")).ShouldBeFalse();
    }

    [Fact]
    public async Task HostRebuildIndex_WithConfigAndNoPath_SaysSoAndExitsNonZero()
    {
        var workingDirectory = await WorkingDirectoryWithAuditAsync("host-cli-config-nopath", calls: 1);

        var (exit, text) = await HostRebuildCli.RunAsync(workingDirectory, ["--config"], Ct);

        exit.ShouldNotBe(0);
        text.ShouldContain("Rebuild failed: --config needs a path.");
        File.Exists(Path.Combine(workingDirectory, "data", "host.db")).ShouldBeFalse();
    }

    [Fact]
    public async Task HostRebuildIndex_ReadsTheAppSettingsFileInTheWorkingDirectory()
    {
        var workingDirectory = await WorkingDirectoryWithAuditAsync("host-cli-settings", calls: 1);
        await HostRebuildCli.WriteConfigAsync(
            Path.Combine(workingDirectory, "appsettings.json"),
            Path.Combine(workingDirectory, "own.db"), Path.Combine(workingDirectory, "data", "audit.jsonl"),
            Path.Combine(workingDirectory, "prompts"), Ct);

        var (exit, text) = await HostRebuildCli.RunAsync(workingDirectory, [], Ct);

        // The settings name own.db, so the index lands there and not in the default ./data/host.db.
        exit.ShouldBe(0, text);
        text.ShouldContain("Rebuild complete: 1 audit entries");
        File.Exists(Path.Combine(workingDirectory, "own.db")).ShouldBeTrue();
        File.Exists(Path.Combine(workingDirectory, "data", "host.db")).ShouldBeFalse();
    }

    [Fact]
    public async Task Repl_WithABlankKey_FailsLoudNamingTheVariable()
    {
        var (exit, text) = await CliRepl.RunWithBlankKeyAsync(Ct);

        exit.ShouldNotBe(0);
        text.ShouldContain(CliRepl.ApiKeyVariable);
        text.ShouldContain("no default provider");
    }
}

#pragma warning disable AMPELUM_V09_PREVIEW
using Shouldly;
using SignalGrid.Ampelum.Host.Tools;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests;

public sealed class HostCliTests
{
    [Fact]
    public async Task RebuildIndex_ValidConfig_ReturnsZero()
    {
        using var tempDir = new TempDirFixture();
        var configPath = Path.Combine(tempDir.Path, "appsettings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        // The command now reads this file, and a backslash path is not valid JSON.
        var slashed = tempDir.Path.Replace('\\', '/');
        var config = $$"""
{
  "Host": {
    "Storage": {
      "SqlitePath": "{{slashed}}/host.db",
      "AuditFile": "{{slashed}}/audit.jsonl",
      "PromptDir": "{{slashed}}/prompts"
    }
  }
}
""";
        File.WriteAllText(configPath, config);
        var result = await HostCli.RunAsync(new[] { "rebuild-index", "--config", configPath }, TestContext.Current.CancellationToken);
        result.ShouldBe(0);
    }

    [Fact]
    public async Task RebuildIndex_Help_ReturnsZero()
    {
        var result = await HostCli.RunAsync(new[] { "--help" }, TestContext.Current.CancellationToken);
        result.ShouldBe(0);
    }

    [Fact]
    public async Task RebuildIndex_NoModules_Succeeds()
    {
        using var tempDir = new TempDirFixture();
        var configPath = Path.Combine(tempDir.Path, "appsettings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var config = "{}";
        File.WriteAllText(configPath, config);
        var result = await HostCli.RunAsync(new[] { "--config", configPath }, TestContext.Current.CancellationToken);
        result.ShouldBe(0);
    }
}

internal sealed class TempDirFixture : IDisposable
{
    public string Path { get; }

    public TempDirFixture()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "host-cli-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { }
    }
}

using SignalGrid.Ampelum.Cli;
using SignalGrid.Ampelum.Cli.Commands;
using Shouldly;
using Xunit;

namespace Ampelum.Cli.Tests;

internal sealed class RecordingProcessRunner : IProcessRunner
{
    public List<(string FileName, IReadOnlyList<string> Args)> Invocations { get; } = [];
    public int ExitCode { get; set; }

    public Task<int> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken ct)
    {
        Invocations.Add((fileName, [.. args]));
        return Task.FromResult(ExitCode);
    }
}

public class NewCommandTests
{
    [Fact]
    public async Task Resolves_TemplateName_AndCallsDotnetNew()
    {
        var runner = new RecordingProcessRunner();
        var config = RootCommandFactory.Create(runner);

        var (exit, _, _) = await StdoutCapture.RunAsync(
            config,
            "new ampelum-backend --name MyApp --output ./src/MyApp",
            TestContext.Current.CancellationToken);

        exit.ShouldBe(0);
        runner.Invocations.Count.ShouldBe(1);
        runner.Invocations[0].FileName.ShouldBe("dotnet");
        runner.Invocations[0].Args.ShouldBe(["new", "ampelum-backend", "--name", "MyApp", "--output", "./src/MyApp"]);
    }

    [Fact]
    public async Task UnknownTemplate_ReturnsExitCode2_WithMessage()
    {
        var runner = new RecordingProcessRunner();
        var config = RootCommandFactory.Create(runner);

        var (exit, _, stderr) = await StdoutCapture.RunAsync(
            config, "new not-a-template", TestContext.Current.CancellationToken);

        exit.ShouldBe(2);
        stderr.ShouldContain("Unknown template");
        runner.Invocations.ShouldBeEmpty();
    }

    [Fact]
    public async Task WithoutOptionalFlags_OnlyTemplate_PassedThrough()
    {
        var runner = new RecordingProcessRunner();
        var config = RootCommandFactory.Create(runner);

        var (exit, _, _) = await StdoutCapture.RunAsync(
            config, "new ampelum-agent", TestContext.Current.CancellationToken);

        exit.ShouldBe(0);
        runner.Invocations[0].Args.ShouldBe(["new", "ampelum-agent"]);
    }
}

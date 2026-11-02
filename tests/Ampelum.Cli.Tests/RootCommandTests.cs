using System.Text.RegularExpressions;
using SignalGrid.Ampelum.Cli.Commands;
using Shouldly;
using Xunit;

namespace Ampelum.Cli.Tests;

public class RootCommandTests
{
    private const string ModelEnvVar = "OPENROUTER_MODEL_1";

    [Fact]
    public async Task Version_FlagPrintsSemver()
    {
        var config = RootCommandFactory.Create();
        var (exit, stdout, _) = await StdoutCapture.RunAsync(
            config, "--version", TestContext.Current.CancellationToken);

        exit.ShouldBe(0);
        var trimmed = stdout.Trim();
        Regex.IsMatch(trimmed, @"^\d+\.\d+\.\d+(-[\w\.]+)?(\+[\w\.]+)?$")
            .ShouldBeTrue($"unexpected --version output: '{trimmed}'");
    }

    [Fact]
    public async Task Help_FlagListsSubcommands()
    {
        var config = RootCommandFactory.Create();
        var (exit, stdout, _) = await StdoutCapture.RunAsync(
            config, "--help", TestContext.Current.CancellationToken);

        exit.ShouldBe(0);
        stdout.ShouldContain("new");
        stdout.ShouldContain("bench");
        stdout.ShouldContain("eval");
        stdout.ShouldContain("repl");
    }

    [Fact]
    public async Task Repl_RunsLoopAgainstInjectedStreams()
    {
        var previous = Environment.GetEnvironmentVariable(ReplCommand.OpenRouterApiKeyEnvVar);
        var previousModel = Environment.GetEnvironmentVariable(ModelEnvVar);
        Environment.SetEnvironmentVariable(ReplCommand.OpenRouterApiKeyEnvVar, "sk-or-v1-test-not-used");
        Environment.SetEnvironmentVariable(ModelEnvVar, "test/placeholder-model");
        try
        {
            var input = new StringReader(":quit\n");
            var output = new StringWriter();
            var error = new StringWriter();
            var cmd = ReplCommand.Create(input, output, error);
            var exit = await cmd.Parse("").InvokeAsync(TestContext.Current.CancellationToken);
            exit.ShouldBe(0);
            output.ToString().ShouldContain("Ampelum REPL");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ReplCommand.OpenRouterApiKeyEnvVar, previous);
            Environment.SetEnvironmentVariable(ModelEnvVar, previousModel);
        }
    }

    [Fact]
    public void ResolveChatClient_WithoutApiKey_ThrowsActionableException()
    {
        var previous = Environment.GetEnvironmentVariable(ReplCommand.OpenRouterApiKeyEnvVar);
        Environment.SetEnvironmentVariable(ReplCommand.OpenRouterApiKeyEnvVar, null);
        try
        {
            var ex = Should.Throw<InvalidOperationException>(() => ReplCommand.ResolveChatClient());
            ex.Message.ShouldContain(ReplCommand.OpenRouterApiKeyEnvVar);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ReplCommand.OpenRouterApiKeyEnvVar, previous);
        }
    }

    [Fact]
    public void ResolveChatClient_WithApiKey_ReturnsClient()
    {
        var previous = Environment.GetEnvironmentVariable(ReplCommand.OpenRouterApiKeyEnvVar);
        var previousModel = Environment.GetEnvironmentVariable(ModelEnvVar);
        Environment.SetEnvironmentVariable(ReplCommand.OpenRouterApiKeyEnvVar, "sk-or-v1-test-not-used");
        Environment.SetEnvironmentVariable(ModelEnvVar, "test/placeholder-model");
        try
        {
            var chat = ReplCommand.ResolveChatClient();
            chat.ShouldNotBeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ReplCommand.OpenRouterApiKeyEnvVar, previous);
            Environment.SetEnvironmentVariable(ModelEnvVar, previousModel);
        }
    }

    [Fact]
    public void ReplCommand_DefaultFactory_BindsConsole()
    {
        var cmd = ReplCommand.Create();
        cmd.Name.ShouldBe("repl");
    }

    [Fact]
    public void ResolveVersion_ReturnsNonEmpty()
    {
        var ver = RootCommandFactory.ResolveVersion();
        ver.ShouldNotBeNullOrWhiteSpace();
    }
}

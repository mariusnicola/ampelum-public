using Ampelum.Samples.SafetyGateway;
using Shouldly;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Agent.Tools;
using SignalGrid.Ampelum.ProviderAdapters;
using Xunit;

namespace Ampelum.Samples.SafetyGateway.Tests;

/// <summary>
/// Behaviour of Ampelum classes the sample builds on. These call the library directly, so they do not
/// exercise the sample; the sample's own tests are in the other files.
/// </summary>
public sealed class AmpelumBuildingBlockTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ChainStore_FallsThroughInOrder_AndWritesToTheFirstStore()
    {
        var first = new InMemorySecretStore();
        var second = new InMemorySecretStore();
        await second.StoreAsync("alias", "from-second", "run-1", Ct);
        var chain = new ChainSecretStore([first, second]);

        (await chain.ResolveAsync("alias", "run-1", Ct)).ShouldBe("from-second");
        await chain.StoreAsync("alias", "from-first", "run-1", Ct);
        (await chain.ResolveAsync("alias", "run-1", Ct)).ShouldBe("from-first");
        (await first.ExistsAsync("alias", "run-1", Ct)).ShouldBeTrue();
        await Should.ThrowAsync<SecretNotFoundException>(() => chain.ResolveAsync("other", "run-1", Ct));
    }

    [Fact]
    public async Task EnvironmentStore_IsReadOnly_AndAnUnsetAliasIsNotFound()
    {
        var store = new EnvironmentSecretStore();
        var alias = $"unset_{Guid.NewGuid():N}";

        await Should.ThrowAsync<SecretNotFoundException>(() => store.ResolveAsync(alias, "run-1", Ct));
        await Should.ThrowAsync<SecretStoreReadOnlyException>(() => store.StoreAsync(alias, "value", "run-1", Ct));
        (await store.ExistsAsync(alias, "run-1", Ct)).ShouldBeFalse();
    }

    [Fact]
    public void Converter_RoundTripsTheSameCallsThroughEveryFormat()
    {
        var calls = ToolCallFormatConverter.FromOpenAi(ProviderToolCallTests.OpenAiJson);

        var viaAnthropic = ToolCallFormatConverter.FromAnthropic(ToolCallFormatConverter.ToAnthropic(calls));
        var viaGemini = ToolCallFormatConverter.FromGemini(ToolCallFormatConverter.ToGemini(calls));
        var viaOpenAi = ToolCallFormatConverter.FromOpenAi(ToolCallFormatConverter.ToOpenAi(calls));

        foreach (var converted in new[] { viaAnthropic, viaGemini, viaOpenAi })
        {
            converted.Select(c => c.Name).ShouldBe(calls.Select(c => c.Name));
            converted.Select(c => c.Arguments.Count).ShouldBe(calls.Select(c => c.Arguments.Count));
        }
        viaAnthropic.Select(c => c.Id).ShouldBe(calls.Select(c => c.Id));
        viaGemini[2].Arguments["command"].ShouldBe("df -h");
    }

    [Fact]
    public void Converter_RejectsJsonThatIsNotAnArray()
    {
        Should.Throw<ArgumentException>(() => ToolCallFormatConverter.FromOpenAi("""{"id":"c1"}"""));
    }

    [Fact]
    public void CostSummaries_AddUp()
    {
        var one = new ChatCostSummary(10, 2, 0.5m);
        var two = new ChatCostSummary(5, 1, 0.25m);

        (one + two).ShouldBe(new ChatCostSummary(15, 3, 0.75m));
        (ChatCostSummary.Zero + one).ShouldBe(one);
    }

    [Fact]
    public async Task ProcessSandbox_RefusesAPolicyItCannotEnforce_WithoutStartingTheProgram()
    {
        var result = await new ProcessSandbox().ExecuteAsync(new SandboxCommand
        {
            Program = "definitely-not-a-program",
            Policy = new SandboxPolicy { Network = new SandboxNetworkPolicy { Access = SandboxNetworkAccess.DenyAll } },
        }, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(SandboxOutcome.Rejected);
        result.RejectionReason!.ShouldContain("no OS-level network or filesystem isolation");
        result.ExitCode.ShouldBe(-1);
    }

    [Fact]
    public async Task ProcessSandbox_RefusesFileCommands_AndOtherIsolationModes()
    {
        var sandbox = new ProcessSandbox();

        var file = await sandbox.ExecuteAsync(
            new SandboxCommand { Kind = SandboxCommandKind.FileWrite, Program = "x" }, TestContext.Current.CancellationToken);
        var container = await sandbox.ExecuteAsync(new SandboxCommand
        {
            Program = "x",
            Policy = new SandboxPolicy { IsolationMode = SandboxIsolationMode.Container },
        }, TestContext.Current.CancellationToken);

        file.Outcome.ShouldBe(SandboxOutcome.Rejected);
        file.RejectionReason!.ShouldContain("Unsupported command kind");
        container.Outcome.ShouldBe(SandboxOutcome.Rejected);
        container.RejectionReason!.ShouldContain("isolation mode not supported");
    }

    [Fact]
    public void CommandParser_KeepsQuotedArgumentsTogether()
    {
        SandboxCommandParser.Tokenize("grep -r \"two words\" src").ShouldBe(["grep", "-r", "two words", "src"]);
        SandboxCommandParser.Tokenize("   ").ShouldBeEmpty();
    }
}

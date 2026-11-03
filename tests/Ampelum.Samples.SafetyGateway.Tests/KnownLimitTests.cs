using Ampelum.Samples.SafetyGateway;
using Shouldly;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Agent.Tools;
using SignalGrid.Ampelum.ProviderAdapters;
using Xunit;

namespace Ampelum.Samples.SafetyGateway.Tests;

/// <summary>Each test here pins something that does not work, so a reader sees it as a limit and a fix shows up as a failing test.</summary>
public sealed class KnownLimitTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task KnownLimit_EphemeralTokens_AreUnsigned_SoAnyoneCanMakeOne_WhileTheSignedProviderRefusesTheSameToken()
    {
        var ephemeral = new EphemeralIdentityProvider();
        var signed = new LocalJwtIdentityProvider();
        const string counterfeit = "helpdesk-assistant:00000000000000000000000000000000";

        var issued = await ephemeral.IssueAsync("helpdesk-assistant", ["tools:information"], TimeSpan.FromMinutes(5), Ct);

        issued.Token.ShouldStartWith("helpdesk-assistant:");
        (await ephemeral.VerifyAsync(counterfeit, Ct)).ShouldNotBeNull();
        (await signed.VerifyAsync(counterfeit, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task KnownLimit_FileTrustStore_KeepsLevelsInMemoryOnly_ANewStoreOnTheSameFolderStartsAtUnknown()
    {
        using var folder = new TempFolder();
        var first = new FileTrustStore(folder.File("trust"));
        await first.SetTrustLevelAsync("helpdesk-assistant", "helpdesk", TrustLevel.Trusted, "set by the test", Ct);

        var second = new FileTrustStore(folder.File("trust"));

        (await first.GetTrustLevelAsync("helpdesk-assistant", "helpdesk", Ct)).ShouldBe(TrustLevel.Trusted);
        (await second.GetTrustLevelAsync("helpdesk-assistant", "helpdesk", Ct)).ShouldBe(TrustLevel.Unknown);
        Directory.GetFiles(folder.File("trust")).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("password: hunter2hunter2")]
    [InlineData("Authorization: Bearer abcdef1234567890abcdef")]
    [InlineData("postgres://user:pw12345@db.example.test/app")]
    [InlineData("0123456789abcdef0123456789abcdef01234567")]
    public async Task KnownLimit_SecretWithoutAKnownKeyShape_ReachesTheModelAndTheTrace(string secret)
    {
        await using var h = await Harness.StartAsync();
        FakeScript.EnqueueAnswer(h.Chat, "Please rotate it.", 40, 5);

        await h.Gateway.AskAsync($"Is it fine to share {secret} here?", Ct);

        // OutboundSanitizer only matches known key shapes, so none of these is cut.
        h.Sent(0).ShouldContain(secret);
        h.Trace.Records.Single().PromptMessages.ShouldContain(m => m.Text.Contains(secret));
    }
}

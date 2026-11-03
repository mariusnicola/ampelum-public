using Shouldly;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace Ampelum.Samples.ChatFrontend.Tests;

public sealed class SettingsAndDemoTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static Func<string, string> Settings(Dictionary<string, string> values, List<string>? asked = null) => key =>
    {
        asked?.Add(key);
        return values.TryGetValue(key, out var value)
            ? value
            : throw new InvalidOperationException($"Missing setting {key}.");
    };

    [Fact]
    public void Live_RequiresTheFallbackModelAndDoesNotBorrowThePrimaryOne()
    {
        var created = false;
        var settings = Settings(new() { ["OPENROUTER_MODEL_1"] = "id-one" });

        var error = Should.Throw<InvalidOperationException>(
            () => ChatBackend.Live(settings, () => { created = true; return new FakeChatClient(); }));

        error.Message.ShouldContain("OPENROUTER_MODEL_2");
        created.ShouldBeFalse();
    }

    [Fact]
    public void Live_RequiresThePrimaryModel()
    {
        var settings = Settings(new() { ["OPENROUTER_MODEL_2"] = "id-two" });

        Should.Throw<InvalidOperationException>(() => ChatBackend.Live(settings, () => new FakeChatClient()))
            .Message.ShouldContain("OPENROUTER_MODEL_1");
    }

    [Fact]
    public async Task Live_SendsTheFirstSettingToThePrimaryAndTheSecondToTheFallback_OnOneSharedClient()
    {
        var asked = new List<string>();
        var shared = new FakeChatClient();
        shared.EnqueueError(new HttpRequestException("primary is down")).EnqueueText("rescued");
        var settings = Settings(new() { ["OPENROUTER_MODEL_1"] = "id-one", ["OPENROUTER_MODEL_2"] = "id-two" }, asked);
        using var backend = ChatBackend.Live(settings, () => shared);

        var reply = await backend.QuickReplyAsync("Hello?", Ct);

        reply.Endpoint.ShouldBe(ChatBackend.FallbackEndpoint);
        shared.RecordedCalls.Select(c => c.Options!.ModelId).ToList().ShouldBe(["id-one", "id-two"]);
        asked.ShouldBe(["OPENROUTER_MODEL_1", "OPENROUTER_MODEL_2"]);
    }

    [Fact]
    public async Task FakeRun_ExercisesEveryPartAndSucceeds()
    {
        var script = new FakeScript();
        using var backend = script.CreateBackend();
        var output = new StringWriter();

        var ok = await ChatDemo.RunAsync(backend, output, script.FailPrimaryNextCall, Ct);

        ok.ShouldBeTrue(output.ToString());
        var text = output.ToString();
        text.ShouldContain("Stream:   5 tokens");
        text.ShouldContain("Fields:   Answer, FollowUp");
        text.ShouldContain("Failover: served by fallback");
        // Conversation, field stream, one typed answer, one quick reply, then the failed call.
        script.Primary.RecordedCalls.Count.ShouldBe(5);
        script.Fallback.RecordedCalls.Count.ShouldBe(1);
    }
}

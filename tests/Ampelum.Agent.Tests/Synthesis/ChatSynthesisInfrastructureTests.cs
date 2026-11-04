using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SignalGrid.Ampelum.Agent.Synthesis;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests.Synthesis;

public sealed class ChatSynthesisInfrastructureTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ampelum-synth-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    static string[] MustSayOk(string content) => content.Contains("ok", StringComparison.Ordinal) ? [] : ["missing ok"];

    [Fact]
    public async Task Generate_RetriesWithTheRejectionReasons_UntilTheValidatorAccepts()
    {
        var chat = new FakeChatClient().EnqueueText("nope").EnqueueText("```yaml\nall ok\n```");
        var infra = new ChatSynthesisInfrastructure(chat);

        var result = await infra.GenerateAndValidateAsync("make it", null, MustSayOk, TestContext.Current.CancellationToken);

        result.Valid.ShouldBeTrue();
        result.AttemptsRequired.ShouldBe(2);
        result.Content.ShouldBe("all ok");
        chat.RecordedCalls.Count.ShouldBe(2);
        chat.RecordedCalls[0].Messages[0].Text.ShouldBe("make it");
        chat.RecordedCalls[1].Messages[0].Text.ShouldContain("- missing ok");
    }

    sealed class CountingHook : SignalGrid.Ampelum.Hooks.IHook
    {
        public int Before;
        public int Order => 0;
        public ValueTask OnBeforeAsync(SignalGrid.Ampelum.Hooks.HookContext ctx, CancellationToken ct) { Before++; return ValueTask.CompletedTask; }
        public ValueTask OnAfterAsync(SignalGrid.Ampelum.Hooks.HookContext ctx, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask OnErrorAsync(SignalGrid.Ampelum.Hooks.HookContext ctx, Exception error, CancellationToken ct) => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Generate_RunsTheAmbientHooks_ForEveryAttempt_OnTheClientItWasGiven()
    {
        var ambientChat = new FakeChatClient();
        var chat = new FakeChatClient().EnqueueText("no").EnqueueText("ok");
        var hook = new CountingHook();
        var context = new AmpelumContext(ambientChat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance).WithHook(hook);
        using var _ = AmpelumContext.Push(context);

        var result = await new ChatSynthesisInfrastructure(chat).GenerateAndValidateAsync("make it", null, MustSayOk, TestContext.Current.CancellationToken);

        result.Valid.ShouldBeTrue();
        hook.Before.ShouldBe(2);
        chat.RecordedCalls.Count.ShouldBe(2);
        ambientChat.RecordedCalls.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Generate_GivesUpAfterTheLastAttempt_AndReturnsTheErrors()
    {
        var chat = new FakeChatClient().EnqueueText("a").EnqueueText("b");
        var infra = new ChatSynthesisInfrastructure(chat, maxAttempts: 2);

        var result = await infra.GenerateAndValidateAsync("make it", null, MustSayOk, TestContext.Current.CancellationToken);

        result.Valid.ShouldBeFalse();
        result.AttemptsRequired.ShouldBe(2);
        result.Content.ShouldBe("b");
        result.ValidationErrors.ShouldBe(["missing ok"]);
    }

    [Fact]
    public async Task WriteVersioned_MovesThePreviousFileToHistory_AndMetricsRoundTrip()
    {
        var infra = new ChatSynthesisInfrastructure(new FakeChatClient());
        var path = Path.Combine(_dir, "flows", "triage.yaml");

        await infra.WriteVersionedAsync(path, "v1", "1.0.0", TestContext.Current.CancellationToken);
        await infra.WriteVersionedAsync(path, "v2", "1.1.0", TestContext.Current.CancellationToken);

        File.ReadAllText(path).ShouldBe("v2");
        File.ReadAllText(Path.Combine(_dir, "flows", ".history", "triage-1.1.0.yaml")).ShouldBe("v1");

        (await infra.ReadMetricsAsync(path, TestContext.Current.CancellationToken)).ShouldBeNull();
        await infra.WriteMetricsAsync(path, new WorkflowMetrics { WorkflowName = "triage", RunCount = 3 }, TestContext.Current.CancellationToken);
        var back = await infra.ReadMetricsAsync(path, TestContext.Current.CancellationToken);
        back!.RunCount.ShouldBe(3);
        File.Exists(path + ".metrics.json.tmp").ShouldBeFalse();
    }
}

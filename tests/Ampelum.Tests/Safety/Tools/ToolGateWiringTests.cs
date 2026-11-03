using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Safety.Tools;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace Ampelum.Tests.Safety.Tools;

/// <summary>The library publishes the tools it offers to the model, so ToolGateHook gates without the caller setting metadata.</summary>
public sealed class ToolGateWiringTests
{
    static InMemoryToolRegistry Registry()
    {
        var r = new InMemoryToolRegistry();
        r.Register(new ToolRegistration("search", "1", ToolCategory.Information));
        r.Register(new ToolRegistration("shell", "1", ToolCategory.SystemControl));
        return r;
    }

    static ToolGateHook Gate() => new(Registry(),
        new ToolGatePolicy(DenyCategories: new HashSet<ToolCategory> { ToolCategory.SystemControl }));

    static AIFunction Fn(string name) => AIFunctionFactory.Create(() => "x", name, name);

    static AmpelumContext Ctx(IChatClient chat, params IHook[] hooks) =>
        new(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance) { Hooks = hooks };

    static IReadOnlyList<ChatMessage> Msgs() => [new ChatMessage(ChatRole.User, "hi")];

    [Fact]
    public async Task HookedLmCall_unregistered_tool_is_refused_before_the_model_is_called()
    {
        var chat = new FakeChatClient().EnqueueText("ok");
        var options = new ChatOptions { Tools = [Fn("missing")] };

        var ex = await Should.ThrowAsync<ForbiddenToolException>(() => HookedLmCall.InvokeAsync(
            Ctx(chat, Gate()), "ReAct", Msgs(), options, TestContext.Current.CancellationToken));

        ex.ToolName.ShouldBe("missing");
        chat.RecordedCalls.Count.ShouldBe(0);
    }

    [Fact]
    public async Task HookedLmCall_policy_denied_tool_is_refused()
    {
        var chat = new FakeChatClient().EnqueueText("ok");
        var options = new ChatOptions { Tools = [Fn("search"), Fn("shell")] };

        var ex = await Should.ThrowAsync<ForbiddenToolException>(() => HookedLmCall.InvokeAsync(
            Ctx(chat, Gate()), "ReAct", Msgs(), options, TestContext.Current.CancellationToken));

        ex.ToolName.ShouldBe("shell");
        chat.RecordedCalls.Count.ShouldBe(0);
    }

    [Fact]
    public async Task HookedLmCall_registered_allowed_tool_passes()
    {
        var chat = new FakeChatClient().EnqueueText("ok");
        var options = new ChatOptions { Tools = [Fn("search")] };

        var response = await HookedLmCall.InvokeAsync(
            Ctx(chat, Gate()), "ReAct", Msgs(), options, TestContext.Current.CancellationToken);

        response.Text.ShouldBe("ok");
    }

    [Fact]
    public async Task HookedLmCall_without_tools_is_not_gated()
    {
        var chat = new FakeChatClient().EnqueueText("ok");

        var response = await HookedLmCall.InvokeAsync(
            Ctx(chat, Gate()), "ReAct", Msgs(), null, TestContext.Current.CancellationToken);

        response.Text.ShouldBe("ok");
    }

    [Signature("")] public sealed record QA : ISignature
    {
        [Input]  public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    [Fact]
    public async Task ReAct_refuses_a_tool_the_registry_does_not_know()
    {
        var chat = new FakeChatClient().EnqueueText("final_answer: Paris");
        var react = new ReAct<QA>(Ctx(chat, Gate()), tools: [Fn("missing")]);

        await Should.ThrowAsync<ForbiddenToolException>(() =>
            react.ForwardAsync(new QA { Question = "?" }, ct: TestContext.Current.CancellationToken));

        chat.RecordedCalls.Count.ShouldBe(0);
    }

    [Fact]
    public async Task StreamingChatSession_refuses_an_unregistered_tool()
    {
        var chat = new FakeChatClient().EnqueueStreamingChunks("hi");
        var session = new StreamingChatSession(Ctx(chat, Gate()), "Sys", tools: [Fn("missing")]);

        await Should.ThrowAsync<ForbiddenToolException>(async () =>
        {
            await foreach (var _ in session.SendAsync("go", TestContext.Current.CancellationToken)) { }
        });
    }

    [Fact]
    public async Task StreamingChatSession_registered_tool_streams()
    {
        var chat = new FakeChatClient().EnqueueStreamingChunks("hi");
        var session = new StreamingChatSession(Ctx(chat, Gate()), "Sys", tools: [Fn("search")]);

        var tokens = new List<string>();
        await foreach (var t in session.SendAsync("go", TestContext.Current.CancellationToken))
            tokens.Add(t);

        tokens.ShouldBe(["hi"]);
    }
}

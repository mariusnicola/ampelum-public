using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace Ampelum.Tests.Hooks;

public sealed class HookedLmCallTests
{
    sealed class SpyHook : IHook
    {
        public int Order { get; init; }
        public List<string> Calls { get; } = new();
        public List<IReadOnlyDictionary<string, object?>> InputsSeen { get; } = new();
        public IReadOnlyDictionary<string, object?>? OutputsToWriteOnBefore { get; init; }

        public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct)
        {
            Calls.Add($"before:{ctx.ModuleName}");
            InputsSeen.Add(ctx.Inputs);
            if (OutputsToWriteOnBefore is not null) ctx.Outputs = OutputsToWriteOnBefore;
            return default;
        }

        public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct)
        {
            Calls.Add($"after:{ctx.ModuleName}");
            return default;
        }

        public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct)
        {
            Calls.Add($"error:{error.GetType().Name}");
            return default;
        }
    }

    static AmpelumContext MakeCtx(FakeChatClient chat, params IHook[] hooks) =>
        new(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
        {
            Hooks = hooks.Length == 0 ? null : hooks,
        };

    static IReadOnlyList<ChatMessage> Messages(params (ChatRole Role, string Text)[] msgs) =>
        msgs.Select(m => new ChatMessage(m.Role, m.Text)).ToArray();

    [Fact]
    public async Task NoHooks_CallsChatDirectly()
    {
        var chat = new FakeChatClient().EnqueueText("hi there");
        var context = MakeCtx(chat);
        var messages = Messages((ChatRole.User, "hello"));

        var response = await HookedLmCall.InvokeAsync(
            context, "ReAct", messages, cancellationToken: TestContext.Current.CancellationToken);

        response.Text.ShouldBe("hi there");
        chat.RecordedCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Hooks_FireInOrder_WithFlattenedStringInputs()
    {
        var chat = new FakeChatClient().EnqueueText(
            "answer text", new UsageDetails { InputTokenCount = 5, OutputTokenCount = 3 });
        var hook = new SpyHook { Order = 0 };
        var context = MakeCtx(chat, hook);
        var messages = Messages(
            (ChatRole.System, "be helpful"),
            (ChatRole.User, "what is 2+2?"));

        var response = await HookedLmCall.InvokeAsync(
            context, "ReAct", messages, cancellationToken: TestContext.Current.CancellationToken);

        response.Text.ShouldBe("answer text");
        hook.Calls.ShouldBe(new[] { "before:ReAct", "after:ReAct" });

        var inputs = hook.InputsSeen.ShouldHaveSingleItem();
        inputs.Count.ShouldBe(2);
        inputs["message.0.system"].ShouldBe("be helpful");
        inputs["message.1.user"].ShouldBe("what is 2+2?");
    }

    [Fact]
    public async Task PiiStyleHook_CanReadMessageTextAsStringValue()
    {
        // PiiRedactionHook/PromptInjectionHook only inspect dictionary VALUES typed as `string`.
        var chat = new FakeChatClient().EnqueueText("ok");
        string? capturedValue = null;
        var hook = new InlineHook(before: ctx =>
        {
            foreach (var kv in ctx.Inputs)
            {
                if (kv.Value is string s) capturedValue = s;
            }
        });
        var context = MakeCtx(chat, hook);
        var messages = Messages((ChatRole.User, "my email is a@b.com"));

        await HookedLmCall.InvokeAsync(context, "ReAct", messages, cancellationToken: TestContext.Current.CancellationToken);

        capturedValue.ShouldBe("my email is a@b.com");
    }

    [Fact]
    public async Task SemanticCacheStyleShortCircuit_SkipsLmCall_StillFiresOnAfter()
    {
        var chat = new FakeChatClient(); // nothing enqueued — a real call would throw
        var hook = new SpyHook
        {
            Order = 0,
            OutputsToWriteOnBefore = new Dictionary<string, object?> { [HookedLmCall.ResponseOutputKey] = "cached answer" },
        };
        var context = MakeCtx(chat, hook);
        var messages = Messages((ChatRole.User, "hi"));

        var response = await HookedLmCall.InvokeAsync(
            context, "ReAct", messages, cancellationToken: TestContext.Current.CancellationToken);

        response.Text.ShouldBe("cached answer");
        hook.Calls.ShouldBe(new[] { "before:ReAct", "after:ReAct" });
        chat.RecordedCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task LmFailure_TriggersOnError_ThenRethrowsLmCallException()
    {
        var chat = new FakeChatClient().EnqueueError(new InvalidOperationException("provider down"));
        var hook = new SpyHook { Order = 0 };
        var context = MakeCtx(chat, hook);
        var messages = Messages((ChatRole.User, "hi"));

        var ex = await Should.ThrowAsync<LmCallException>(
            () => HookedLmCall.InvokeAsync(context, "ReAct", messages, cancellationToken: TestContext.Current.CancellationToken));

        ex.InnerException.ShouldBeOfType<InvalidOperationException>();
        hook.Calls.ShouldBe(new[] { "before:ReAct", "error:LmCallException" });
    }

    [Fact]
    public async Task LmCancellation_TriggersOnError_ThenRethrowsOperationCanceledException_NotWrapped()
    {
        var chat = new FakeChatClient().EnqueueError(new OperationCanceledException("cancelled"));
        var hook = new SpyHook { Order = 0 };
        var context = MakeCtx(chat, hook);
        var messages = Messages((ChatRole.User, "hi"));

        await Should.ThrowAsync<OperationCanceledException>(
            () => HookedLmCall.InvokeAsync(context, "ReAct", messages, cancellationToken: TestContext.Current.CancellationToken));

        hook.Calls.Count.ShouldBe(2);
        hook.Calls[0].ShouldBe("before:ReAct");
        hook.Calls[1].ShouldStartWith("error:");
    }

    [Fact]
    public async Task SuccessPath_SetsMetadataOnHookContext()
    {
        var chat = new FakeChatClient().EnqueueText(
            "answer", new UsageDetails { InputTokenCount = 11, OutputTokenCount = 4 }, modelId: "gpt-test", responseId: "r1");
        IReadOnlyDictionary<string, object?>? metadataSnapshot = null;
        var hook = new InlineHook(after: ctx => metadataSnapshot = ctx.Metadata.ToDictionary(kv => kv.Key, kv => kv.Value));
        var context = MakeCtx(chat, hook);
        var messages = Messages((ChatRole.User, "hi"));

        await HookedLmCall.InvokeAsync(context, "ReAct", messages, cancellationToken: TestContext.Current.CancellationToken);

        var snap = metadataSnapshot.ShouldNotBeNull();
        snap[HookMetadataKeys.ModelId].ShouldBe("gpt-test");
        snap[HookMetadataKeys.PromptTokens].ShouldBe(11L);
        snap[HookMetadataKeys.CompletionTokens].ShouldBe(4L);
        snap.ShouldContainKey(HookMetadataKeys.LmCallDurationMs);
    }

    [Fact]
    public async Task ReturnsOriginalResponseObject_NotReconstructed()
    {
        var raw = new ChatResponse(new ChatMessage(ChatRole.Assistant, "hi"))
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["tool_call_marker"] = true },
        };
        var chat = new FakeChatClient().EnqueueRaw(raw);
        var hook = new SpyHook { Order = 0 };
        var context = MakeCtx(chat, hook);
        var messages = Messages((ChatRole.User, "hi"));

        var response = await HookedLmCall.InvokeAsync(context, "ReAct", messages, cancellationToken: TestContext.Current.CancellationToken);

        ReferenceEquals(response, raw).ShouldBeTrue();
        response.AdditionalProperties!["tool_call_marker"].ShouldBe(true);
    }

    sealed class InlineHook(
        Action<HookContext>? before = null,
        Action<HookContext>? after = null,
        Action<HookContext, Exception>? error = null) : IHook
    {
        public int Order => 0;

        public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct)
        {
            before?.Invoke(ctx);
            return default;
        }

        public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct)
        {
            after?.Invoke(ctx);
            return default;
        }

        public ValueTask OnErrorAsync(HookContext ctx, Exception err, CancellationToken ct)
        {
            error?.Invoke(ctx, err);
            return default;
        }
    }
}

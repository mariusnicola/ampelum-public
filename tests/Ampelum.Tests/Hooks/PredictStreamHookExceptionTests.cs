using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Hooks;

/// <summary>
/// Covers the OnBefore-throw and OnAfter-throw paths in Predict.StreamAsync:
///   - Before-hook exception sets terminalError, skips LM call (lines 512-515).
///   - After-hook exception sets terminalError, surfaces via re-throw (lines 622-625).
/// </summary>
public sealed class PredictStreamHookExceptionTests
{
    [Signature("Q&A")]
    public sealed record Sig : ISignature
    {
        [Input] public required string Q { get; init; }
        [Output] public string A { get; init; } = "";
    }

    sealed class ThrowingBeforeHook : IHook
    {
        public int Order => 0;
        public ValueTask OnBeforeAsync(HookContext c, CancellationToken ct) =>
            throw new InvalidOperationException("before-boom");
        public ValueTask OnAfterAsync(HookContext c, CancellationToken ct) => default;
        public ValueTask OnErrorAsync(HookContext c, Exception e, CancellationToken ct) => default;
    }

    sealed class ThrowingAfterHook : IHook
    {
        public int Order => 0;
        public ValueTask OnBeforeAsync(HookContext c, CancellationToken ct) => default;
        public ValueTask OnAfterAsync(HookContext c, CancellationToken ct) =>
            throw new InvalidOperationException("after-boom");
        public ValueTask OnErrorAsync(HookContext c, Exception e, CancellationToken ct) => default;
    }

    static AmpelumContext MakeCtx(FakeChatClient chat, IHook hook) =>
        new(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
        { Hooks = [hook] };

    [Fact]
    public async Task StreamAsync_BeforeHookThrows_PropagatesException()
    {
        var chat = new FakeChatClient().EnqueueText("[[ ## A ## ]]\nwill not be reached");
        var predict = new Predict<Sig>(MakeCtx(chat, new ThrowingBeforeHook()));

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in predict.StreamAsync(
                new Sig { Q = "hi" }, ct: TestContext.Current.CancellationToken)) { }
        });

        ex.Message.ShouldBe("before-boom");
    }

    [Fact]
    public async Task StreamAsync_AfterHookThrows_PropagatesException()
    {
        var chat = new FakeChatClient().EnqueueStreamingOutputs(new Sig { Q = "hi", A = "ok" });
        var predict = new Predict<Sig>(MakeCtx(chat, new ThrowingAfterHook()));

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in predict.StreamAsync(
                new Sig { Q = "hi" }, ct: TestContext.Current.CancellationToken)) { }
        });

        ex.Message.ShouldBe("after-boom");
    }
}

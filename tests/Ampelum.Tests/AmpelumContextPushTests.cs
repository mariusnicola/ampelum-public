using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

public sealed class AmpelumContextPushTests
{
    [Signature("Echo.")]
    public sealed record Echo : ISignature
    {
        [Input]  public required string Text { get; init; }
        [Output] public string Out { get; init; } = "";
    }

    static AmpelumContext MakeCtx(FakeChatClient chat) => new(
        chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);

    [Fact] public void Ambient_IsNull_WhenNothingPushed() =>
        AmpelumContext.Ambient.ShouldBeNull();

    [Fact]
    public void Push_SetsAmbient_UntilDisposed()
    {
        var ctx = MakeCtx(new FakeChatClient());
        AmpelumContext? seen;
        using (AmpelumContext.Push(ctx))
            seen = AmpelumContext.Ambient;
        seen.ShouldBeSameAs(ctx);
        AmpelumContext.Ambient.ShouldBeNull();
    }

    [Fact]
    public void Push_IsReentrant_LifoRestore()
    {
        var a = MakeCtx(new FakeChatClient());
        var b = MakeCtx(new FakeChatClient());
        using (AmpelumContext.Push(a))
        {
            AmpelumContext.Ambient.ShouldBeSameAs(a);
            using (AmpelumContext.Push(b))
                AmpelumContext.Ambient.ShouldBeSameAs(b);
            AmpelumContext.Ambient.ShouldBeSameAs(a);
        }
        AmpelumContext.Ambient.ShouldBeNull();
    }

    [Fact]
    public async Task Ambient_FlowsAcrossAwait()
    {
        var ctx = MakeCtx(new FakeChatClient());
        using var _ = AmpelumContext.Push(ctx);
        await Task.Yield();
        AmpelumContext.Ambient.ShouldBeSameAs(ctx);
    }

    [Fact]
    public void Push_ThrowsOnNullCtx() =>
        Should.Throw<ArgumentNullException>(() => AmpelumContext.Push(null!));

    [Fact]
    public void Push_Scope_IsIdempotent_OnDoubleDispose()
    {
        var ctx = MakeCtx(new FakeChatClient());
        var scope = AmpelumContext.Push(ctx);
        scope.Dispose();
        scope.Dispose(); // second call is a no-op
        AmpelumContext.Ambient.ShouldBeNull();
    }
}

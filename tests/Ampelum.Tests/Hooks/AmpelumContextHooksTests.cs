using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Hooks;

public sealed class AmpelumContextHooksTests
{
    sealed class NoopHook(int order) : IHook
    {
        public int Order => order;
        public ValueTask OnBeforeAsync(HookContext c, CancellationToken ct) => default;
        public ValueTask OnAfterAsync(HookContext c, CancellationToken ct) => default;
        public ValueTask OnErrorAsync(HookContext c, Exception e, CancellationToken ct) => default;
    }

    static AmpelumContext MakeBase() => new(
        new FakeChatClient(),
        new ChatAdapter(),
        NullTraceSink.Instance,
        NullLoggerFactory.Instance);

    [Fact]
    public void Hooks_DefaultsToNull_BackwardCompatible()
    {
        MakeBase().Hooks.ShouldBeNull();
    }

    [Fact]
    public void HooksInit_StoresList()
    {
        var hook = new NoopHook(5);
        var ctx = MakeBase() with { Hooks = new[] { (IHook)hook } };
        ctx.Hooks.ShouldNotBeNull();
        ctx.Hooks!.Count.ShouldBe(1);
        ctx.Hooks[0].ShouldBeSameAs(hook);
    }

    [Fact]
    public void WithHook_AppendsToExistingHooks()
    {
        var a = new NoopHook(0);
        var b = new NoopHook(10);
        var ctx = MakeBase() with { Hooks = new[] { (IHook)a } };
        var ctx2 = ctx.WithHook(b);

        ctx.Hooks!.Count.ShouldBe(1);
        ctx2.Hooks!.Count.ShouldBe(2);
        ctx2.Hooks[0].ShouldBeSameAs(a);
        ctx2.Hooks[1].ShouldBeSameAs(b);
    }

    [Fact]
    public void WithHook_FromNullHooks_StartsList()
    {
        var b = new NoopHook(10);
        var ctx2 = MakeBase().WithHook(b);
        ctx2.Hooks!.Count.ShouldBe(1);
        ctx2.Hooks[0].ShouldBeSameAs(b);
    }

    [Fact]
    public void WithHook_NullArg_Throws()
    {
        Should.Throw<ArgumentNullException>(() => MakeBase().WithHook(null!));
    }
}

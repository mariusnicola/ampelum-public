using SignalGrid.Ampelum;
using SignalGrid.Ampelum.DependencyInjection;
using SignalGrid.Ampelum.Hooks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Hooks;

public sealed class AmpelumBuilderAddHookTests
{
    abstract class NoopHook(int order) : IHook
    {
        public int Order { get; } = order;
        public ValueTask OnBeforeAsync(HookContext c, CancellationToken ct) => default;
        public ValueTask OnAfterAsync(HookContext c, CancellationToken ct) => default;
        public ValueTask OnErrorAsync(HookContext c, Exception e, CancellationToken ct) => default;
    }
    sealed class HookA() : NoopHook(0) { }
    sealed class HookB() : NoopHook(50) { }

    static IServiceProvider Build(Action<AmpelumBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<IChatClient>(new SignalGrid.Ampelum.Testing.FakeChatClient());
        var b = services.AddAmpelum();
        configure?.Invoke(b);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddDspyHook_RegistersAsIHook()
    {
        var sp = Build(b => b.AddAmpelumHook<HookA>());
        var hooks = sp.GetServices<IHook>().ToList();
        hooks.Count.ShouldBe(1);
        hooks[0].ShouldBeOfType<HookA>();
    }

    [Fact]
    public void AddDspyHook_AllowsMultipleRegistrations()
    {
        var sp = Build(b => b.AddAmpelumHook<HookA>().AddAmpelumHook<HookB>());
        var hooks = sp.GetServices<IHook>().ToList();
        hooks.Count.ShouldBe(2);
    }

    [Fact]
    public void AmpelumContext_Hooks_PopulatedFromDI_SortedAscending()
    {
        var sp = Build(b => b.AddAmpelumHook<HookB>().AddAmpelumHook<HookA>());
        using var scope = sp.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AmpelumContext>();
        ctx.Hooks.ShouldNotBeNull();
        ctx.Hooks!.Count.ShouldBe(2);
        ctx.Hooks[0].ShouldBeOfType<HookA>();
        ctx.Hooks[1].ShouldBeOfType<HookB>();
    }

    [Fact]
    public void AmpelumContext_Hooks_NullWhenNoneRegistered()
    {
        var sp = Build();
        using var scope = sp.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AmpelumContext>();
        ctx.Hooks.ShouldBeNull();
    }
}

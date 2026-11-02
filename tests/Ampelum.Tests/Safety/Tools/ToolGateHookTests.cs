using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Safety.Tools;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Safety.Tools;

public sealed class ToolGateHookTests
{
    static InMemoryToolRegistry NewRegistry()
    {
        var r = new InMemoryToolRegistry();
        r.Register(new ToolRegistration("search", "1", ToolCategory.Information));
        r.Register(new ToolRegistration("shell", "1", ToolCategory.SystemControl));
        return r;
    }

    static HookContext NewCtx() => new() { ModuleName = "Predict", Inputs = new Dictionary<string, object?>() };

    [Fact]
    public async Task OnBefore_no_tools_requested_is_noop()
    {
        var sut = new ToolGateHook(NewRegistry(), ToolGatePolicy.Open);
        var ctx = NewCtx();
        await sut.OnBeforeAsync(ctx, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OnBefore_allows_when_default_policy_open_and_tool_registered()
    {
        var sut = new ToolGateHook(NewRegistry(), ToolGatePolicy.Open);
        var ctx = NewCtx();
        ctx.Set(ToolGateHook.RequestedKey, (IEnumerable<(string, string)>)new[] { ("search", "1") });
        await sut.OnBeforeAsync(ctx, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OnBefore_throws_when_tool_not_in_registry()
    {
        var sut = new ToolGateHook(NewRegistry(), ToolGatePolicy.Open);
        var ctx = NewCtx();
        ctx.Set(ToolGateHook.RequestedKey, (IEnumerable<(string, string)>)new[] { ("missing", "1") });
        var ex = await Should.ThrowAsync<ForbiddenToolException>(
            async () => await sut.OnBeforeAsync(ctx, TestContext.Current.CancellationToken));
        ex.ToolName.ShouldBe("missing");
        ex.Reason.ShouldContain("registry");
    }

    [Fact]
    public async Task OnBefore_throws_when_default_policy_denies()
    {
        var policy = new ToolGatePolicy(DenyCategories: new HashSet<ToolCategory> { ToolCategory.SystemControl });
        var sut = new ToolGateHook(NewRegistry(), policy);
        var ctx = NewCtx();
        ctx.Set(ToolGateHook.RequestedKey, (IEnumerable<(string, string)>)new[] { ("shell", "1") });
        var ex = await Should.ThrowAsync<ForbiddenToolException>(
            async () => await sut.OnBeforeAsync(ctx, TestContext.Current.CancellationToken));
        ex.Reason.ShouldContain("policy");
    }

    [Fact]
    public async Task OnBefore_per_context_override_wins()
    {
        // Default policy denies SystemControl; override opens it for this call.
        var defaultPolicy = new ToolGatePolicy(DenyCategories: new HashSet<ToolCategory> { ToolCategory.SystemControl });
        var sut = new ToolGateHook(NewRegistry(), defaultPolicy);
        var ctx = NewCtx();
        ctx.Set(ToolGateHook.RequestedKey, (IEnumerable<(string, string)>)new[] { ("shell", "1") });
        ctx.Set(ToolGateHook.PolicyOverrideKey, ToolGatePolicy.Open);
        await sut.OnBeforeAsync(ctx, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Order_is_200() => new ToolGateHook(NewRegistry(), ToolGatePolicy.Open).Order.ShouldBe(200);

    [Fact]
    public async Task OnAfter_and_OnError_are_noops()
    {
        var sut = new ToolGateHook(NewRegistry(), ToolGatePolicy.Open);
        var ctx = NewCtx();
        await sut.OnAfterAsync(ctx, TestContext.Current.CancellationToken);
        await sut.OnErrorAsync(ctx, new Exception(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void DI_extension_registers_singleton()
    {
        var services = new ServiceCollection();
        services.AddAmpelumToolRegistry(r => r.Register(new ToolRegistration("t", "1", ToolCategory.Information)));
        var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<IToolRegistry>();
        registry.TryGet("t", "1", out var got).ShouldBeTrue();
        got.ShouldNotBeNull();
    }
}

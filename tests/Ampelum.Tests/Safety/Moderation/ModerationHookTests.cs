using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Safety.Moderation;
using Ampelum.Tests.Safety.Moderation.Fixtures;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Safety.Moderation;

public sealed class ModerationHookTests
{
    [Fact]
    public async Task OnAfter_throws_ModerationException_when_moderator_flags()
    {
        var sut = new ModerationHook(new StubModerator(true, "hate"));
        var ctx = new HookContext
        {
            ModuleName = "Predict",
            Inputs = new Dictionary<string, object?>(),
            Outputs = new Dictionary<string, object?> { ["answer"] = "x" },
        };
        await Should.ThrowAsync<ModerationException>(
            async () => await sut.OnAfterAsync(ctx, TestContext.Current.CancellationToken));
        ctx.Metadata[ModerationHook.FlaggedKey].ShouldBe(true);
        ctx.Metadata[ModerationHook.CategoriesKey].ShouldBe("hate");
    }

    [Fact]
    public async Task OnAfter_does_not_throw_when_moderator_clean()
    {
        var sut = new ModerationHook(new StubModerator(false));
        var ctx = new HookContext
        {
            ModuleName = "Predict",
            Inputs = new Dictionary<string, object?>(),
            Outputs = new Dictionary<string, object?> { ["answer"] = "x" },
        };
        await sut.OnAfterAsync(ctx, TestContext.Current.CancellationToken);
        ctx.Metadata[ModerationHook.FlaggedKey].ShouldBe(false);
    }

    [Fact]
    public async Task OnAfter_outputs_null_is_noop()
    {
        var sut = new ModerationHook(new StubModerator(true));
        var ctx = new HookContext { ModuleName = "Predict", Inputs = new Dictionary<string, object?>(), Outputs = null };
        await sut.OnAfterAsync(ctx, TestContext.Current.CancellationToken);
        ctx.Metadata.ContainsKey(ModerationHook.FlaggedKey).ShouldBeFalse();
    }

    [Fact]
    public async Task OnAfter_failOpen_swallows_transport_error()
    {
        var sut = new ModerationHook(new ThrowingModerator(new InvalidOperationException("boom")), failOpen: true);
        var ctx = new HookContext
        {
            ModuleName = "Predict",
            Inputs = new Dictionary<string, object?>(),
            Outputs = new Dictionary<string, object?> { ["answer"] = "x" },
        };
        await sut.OnAfterAsync(ctx, TestContext.Current.CancellationToken);
        ctx.Metadata[ModerationHook.TransportErrorKey].ShouldBe(nameof(InvalidOperationException));
    }

    [Fact]
    public async Task OnAfter_failClosed_propagates_transport_error()
    {
        var sut = new ModerationHook(new ThrowingModerator(new InvalidOperationException("boom")));
        var ctx = new HookContext
        {
            ModuleName = "Predict",
            Inputs = new Dictionary<string, object?>(),
            Outputs = new Dictionary<string, object?> { ["answer"] = "x" },
        };
        await Should.ThrowAsync<InvalidOperationException>(
            async () => await sut.OnAfterAsync(ctx, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Order_is_300() => new ModerationHook(new StubModerator(false)).Order.ShouldBe(300);

    [Fact]
    public async Task OnBefore_and_OnError_are_noops()
    {
        var sut = new ModerationHook(new StubModerator(false));
        var ctx = new HookContext { ModuleName = "Predict", Inputs = new Dictionary<string, object?>() };
        await sut.OnBeforeAsync(ctx, TestContext.Current.CancellationToken);
        await sut.OnErrorAsync(ctx, new Exception(), TestContext.Current.CancellationToken);
    }
}

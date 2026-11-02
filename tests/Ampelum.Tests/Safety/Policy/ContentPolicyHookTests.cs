using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Safety.Policy;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Safety.Policy;

public sealed class ContentPolicyHookTests
{
    static HookContext NewCtx(IReadOnlyDictionary<string, object?>? outputs = null) =>
        new()
        {
            ModuleName = "Predict",
            Inputs = new Dictionary<string, object?>(),
            Outputs = outputs,
        };

    [Fact]
    public async Task OnAfter_outputs_null_is_noop()
    {
        var policy = new ContentPolicyBuilder().Reject("R", new Regex("x")).Build();
        var sut = new ContentPolicyHook(policy);
        var ctx = NewCtx(null);
        await sut.OnAfterAsync(ctx, TestContext.Current.CancellationToken);
        ctx.Outputs.ShouldBeNull();
    }

    [Fact]
    public async Task OnAfter_empty_policy_is_noop()
    {
        var sut = new ContentPolicyHook(ContentPolicy.Empty);
        var ctx = NewCtx(new Dictionary<string, object?> { ["k"] = "v" });
        await sut.OnAfterAsync(ctx, TestContext.Current.CancellationToken);
        ctx.Outputs!["k"].ShouldBe("v");
    }

    [Fact]
    public async Task OnAfter_Reject_throws_ContentPolicyViolationException()
    {
        var policy = new ContentPolicyBuilder().Reject("NO_KEY", new Regex(@"sk-\w+")).Build();
        var sut = new ContentPolicyHook(policy);
        var ctx = NewCtx(new Dictionary<string, object?> { ["a"] = "leak sk-abc123" });
        var ex = await Should.ThrowAsync<ContentPolicyViolationException>(
            async () => await sut.OnAfterAsync(ctx, TestContext.Current.CancellationToken));
        ex.RuleId.ShouldBe("NO_KEY");
        ex.Excerpt.ShouldStartWith("sk-");
    }

    [Fact]
    public async Task OnAfter_Redact_replaces_match_in_place()
    {
        var policy = new ContentPolicyBuilder().Redact("MASK", new Regex(@"secret"), "<MASKED>").Build();
        var sut = new ContentPolicyHook(policy);
        var ctx = NewCtx(new Dictionary<string, object?> { ["a"] = "the secret is out" });
        await sut.OnAfterAsync(ctx, TestContext.Current.CancellationToken);
        ctx.Outputs!["a"].ShouldBe("the <MASKED> is out");
    }

    [Fact]
    public async Task OnAfter_Redact_uses_default_token_when_null()
    {
        // Construct rule directly so RedactionToken is null.
        var rule = new OutputRule("M", new Regex("x"), OutputRuleAction.Redact);
        var policy = new ContentPolicyBuilder();
        // Use Build via builder API after manually injecting through reflection-safe path:
        // simplest is to wrap our own policy. Instead, build via Redact then mutate.
        // We expose ContentPolicy ctor as internal â€” use a direct rule list shim.
        var p = new ContentPolicyBuilder().Redact("M", new Regex("x"), token: null!).Build();
        var sut = new ContentPolicyHook(p);
        var ctx = NewCtx(new Dictionary<string, object?> { ["a"] = "axb" });
        await sut.OnAfterAsync(ctx, TestContext.Current.CancellationToken);
        ctx.Outputs!["a"].ShouldBe("a<REDACTED>b");
    }

    [Fact]
    public async Task OnAfter_Warn_sets_metadata_keys_and_does_not_mutate()
    {
        var policy = new ContentPolicyBuilder().Warn("INTERNAL", new Regex(@"corp\.local")).Build();
        var sut = new ContentPolicyHook(policy);
        var ctx = NewCtx(new Dictionary<string, object?> { ["a"] = "host: corp.local" });
        await sut.OnAfterAsync(ctx, TestContext.Current.CancellationToken);
        ctx.Outputs!["a"].ShouldBe("host: corp.local");
        ctx.Metadata[ContentPolicyHook.WarnedKey].ShouldBe(true);
        ((string)ctx.Metadata[ContentPolicyHook.WarnedRulesKey]!).ShouldBe("INTERNAL");
    }

    [Fact]
    public async Task OnAfter_two_rules_on_same_field_redact_then_warn()
    {
        var policy = new ContentPolicyBuilder()
            .Redact("R", new Regex("foo"), "<F>")
            .Warn("W", new Regex("bar"))
            .Build();
        var sut = new ContentPolicyHook(policy);
        var ctx = NewCtx(new Dictionary<string, object?> { ["a"] = "foo and bar" });
        await sut.OnAfterAsync(ctx, TestContext.Current.CancellationToken);
        ctx.Outputs!["a"].ShouldBe("<F> and bar");
        ctx.Metadata[ContentPolicyHook.WarnedKey].ShouldBe(true);
    }

    [Fact]
    public async Task OnAfter_passes_through_non_string_values()
    {
        var policy = new ContentPolicyBuilder().Reject("R", new Regex("x")).Build();
        var sut = new ContentPolicyHook(policy);
        var ctx = NewCtx(new Dictionary<string, object?> { ["count"] = 42, ["msg"] = "ok" });
        await sut.OnAfterAsync(ctx, TestContext.Current.CancellationToken);
        ctx.Outputs!["count"].ShouldBe(42);
    }

    [Fact]
    public void Order_is_250() =>
        new ContentPolicyHook(ContentPolicy.Empty).Order.ShouldBe(250);

    [Fact]
    public async Task OnBefore_and_OnError_are_noops()
    {
        var sut = new ContentPolicyHook(ContentPolicy.Empty);
        var ctx = NewCtx();
        await sut.OnBeforeAsync(ctx, TestContext.Current.CancellationToken);
        await sut.OnErrorAsync(ctx, new Exception(), TestContext.Current.CancellationToken);
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Hooks;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Safety.Retry;

public sealed class RetryHookSchemaRecoveryTests
{
    static AdapterParseException ParseEx(string hint = "missing close brace", string raw = "{ \"x\": 1") =>
        new("Sig", "JsonAdapter", raw, hint);

    [Fact]
    public async Task OnError_with_AdapterParseException_writes_corrective_prompt_and_increments_recovery_attempt()
    {
        var sut = new RetryHook(maxRecoveryAttempts: 2);
        var ctx = new HookContext { ModuleName = "Predict", Inputs = new Dictionary<string, object?>() };
        await sut.OnErrorAsync(ctx, ParseEx(), TestContext.Current.CancellationToken);

        ctx.Metadata["retry.recovery_attempt"].ShouldBe(1);
        ctx.Metadata["retry.max_recovery_attempts"].ShouldBe(2);
        ctx.Metadata["retry.recovered"].ShouldBe(true);
        var corrective = (string)ctx.Metadata["retry.corrective_prompt"]!;
        corrective.ShouldContain("Your previous response could not be parsed");
        corrective.ShouldContain("missing close brace");
        corrective.ShouldContain("{ \"x\": 1");
    }

    [Fact]
    public async Task OnError_with_AdapterParseException_caps_at_max_recovery_attempts()
    {
        var sut = new RetryHook(maxRecoveryAttempts: 1);
        var ctx = new HookContext { ModuleName = "Predict", Inputs = new Dictionary<string, object?>() };
        await sut.OnErrorAsync(ctx, ParseEx(), TestContext.Current.CancellationToken);
        await sut.OnErrorAsync(ctx, ParseEx(), TestContext.Current.CancellationToken);

        ctx.Metadata["retry.recovery_attempt"].ShouldBe(2);
        ctx.Metadata["retry.recovered"].ShouldBe(false);
    }

    [Fact]
    public async Task Constructor_caps_max_recovery_attempts_at_ceiling()
    {
        var sut = new RetryHook(maxRecoveryAttempts: 999);
        var ctx = new HookContext { ModuleName = "Predict", Inputs = new Dictionary<string, object?>() };
        await sut.OnErrorAsync(ctx, ParseEx(), TestContext.Current.CancellationToken);
        ctx.Metadata["retry.max_recovery_attempts"].ShouldBe(RetryHook.RecoveryAttemptsCeiling);
    }

    [Fact]
    public async Task OnError_with_non_parse_exception_falls_through_to_back_off_branch()
    {
        var sut = new RetryHook();
        var ctx = new HookContext { ModuleName = "Predict", Inputs = new Dictionary<string, object?>() };
        await sut.OnErrorAsync(ctx, new InvalidOperationException("transient"), TestContext.Current.CancellationToken);

        ctx.Metadata["retry.attempt"].ShouldBe(1);
        ctx.Metadata.ContainsKey("retry.corrective_prompt").ShouldBeFalse();
    }

    [Fact]
    public async Task OnError_with_AdapterParseException_uses_unavailable_when_RawResponse_empty()
    {
        var sut = new RetryHook();
        var ctx = new HookContext { ModuleName = "Predict", Inputs = new Dictionary<string, object?>() };
        await sut.OnErrorAsync(ctx, ParseEx(raw: ""), TestContext.Current.CancellationToken);
        ((string)ctx.Metadata["retry.corrective_prompt"]!).ShouldContain("<unavailable>");
    }
}

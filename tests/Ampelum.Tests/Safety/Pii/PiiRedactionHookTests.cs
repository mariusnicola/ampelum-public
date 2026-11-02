using System.Collections.Generic;
using System.Threading.Tasks;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Safety.Pii;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Safety.Pii;

public sealed class PiiRedactionHookTests
{
    [Fact]
    public async Task OnBefore_writes_redacted_inputs_when_string_value_contains_email()
    {
        var sut = new PiiRedactionHook(new RegexPiiRedactor());
        var ctx = new HookContext
        {
            ModuleName = "Predict",
            Inputs = new Dictionary<string, object?>
            {
                ["question"] = "ping alice@example.com",
            },
        };
        await sut.OnBeforeAsync(ctx, TestContext.Current.CancellationToken);

        ctx.Metadata[PiiRedactionHook.RedactedFlagKey].ShouldBe(true);
        var redacted = (IReadOnlyDictionary<string, object?>)ctx.Metadata[PiiRedactionHook.RedactedInputsKey]!;
        redacted["question"].ShouldBe("ping <REDACTED:EMAIL>");
    }

    [Fact]
    public async Task OnAfter_rewrites_outputs_in_place_with_redactions()
    {
        var sut = new PiiRedactionHook(new RegexPiiRedactor());
        var ctx = new HookContext
        {
            ModuleName = "Predict",
            Inputs = new Dictionary<string, object?>(),
            Outputs = new Dictionary<string, object?> { ["answer"] = "call (415) 555-0142" },
        };
        await sut.OnAfterAsync(ctx, TestContext.Current.CancellationToken);
        ctx.Outputs!["answer"].ShouldBe("call <REDACTED:PHONE>");
        ctx.Metadata[PiiRedactionHook.RedactedFlagKey].ShouldBe(true);
    }

    [Fact]
    public async Task OnAfter_outputs_null_is_noop()
    {
        var sut = new PiiRedactionHook(new RegexPiiRedactor());
        var ctx = new HookContext
        {
            ModuleName = "Predict",
            Inputs = new Dictionary<string, object?>(),
            Outputs = null,
        };
        await sut.OnAfterAsync(ctx, TestContext.Current.CancellationToken);
        ctx.Outputs.ShouldBeNull();
        ctx.Metadata.ContainsKey(PiiRedactionHook.RedactedFlagKey).ShouldBeFalse();
    }

    [Fact]
    public async Task OnError_is_noop()
    {
        var sut = new PiiRedactionHook(new RegexPiiRedactor());
        var ctx = new HookContext { ModuleName = "Predict", Inputs = new Dictionary<string, object?>() };
        await sut.OnErrorAsync(ctx, new System.InvalidOperationException("x"), TestContext.Current.CancellationToken);
        ctx.Metadata.Count.ShouldBe(0);
    }

    [Fact]
    public void Order_is_50() => new PiiRedactionHook(new RegexPiiRedactor()).Order.ShouldBe(50);
}

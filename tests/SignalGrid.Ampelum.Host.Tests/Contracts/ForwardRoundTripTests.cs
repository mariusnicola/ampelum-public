#pragma warning disable AMPELUM_V09_PREVIEW
using System.Text.Json;
using Shouldly;
using SignalGrid.Ampelum.Host.Contracts;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.Contracts;

public sealed class ForwardRoundTripTests
{
    private static readonly JsonSerializerOptions Options =
        AmpelumHostContractsJsonContext.Default.Options;

    // ──────────────────────── BudgetHint ────────────────────────

    [Fact]
    public void BudgetHint_RoundTrip_AllFields()
    {
        var src = new BudgetHint(TokenLimit: 1000, CostCentsLimit: 9.99m, CallLimit: 5, DeadlineSeconds: 30);
        var json = JsonSerializer.Serialize(src, Options);
        var dst  = JsonSerializer.Deserialize<BudgetHint>(json, Options);
        dst.ShouldBe(src);
    }

    [Fact]
    public void BudgetHint_RoundTrip_Defaults_NullsOmitted()
    {
        var src  = new BudgetHint();
        var json = JsonSerializer.Serialize(src, Options);
        json.ShouldBe("{}");
        var dst = JsonSerializer.Deserialize<BudgetHint>(json, Options);
        dst.ShouldBe(src);
    }

    [Fact]
    public void BudgetHint_CamelCase()
    {
        var src  = new BudgetHint(TokenLimit: 42);
        var json = JsonSerializer.Serialize(src, Options);
        json.ShouldContain("\"tokenLimit\"");
        json.Contains("\"TokenLimit\"").ShouldBeFalse();
    }

    // ──────────────────────── ForwardRequest ────────────────────────

    [Fact]
    public void ForwardRequest_RoundTrip_AllFields()
    {
        var inputs = new Dictionary<string, JsonElement>
        {
            ["question"] = JsonDocument.Parse("\"hello\"").RootElement,
        }.AsReadOnly();

        var src = new ForwardRequest(
            Inputs: inputs,
            PromptVersionId: "v-abc",
            ParentTraceId: "t-123",
            Budget: new BudgetHint(TokenLimit: 500));

        var json = JsonSerializer.Serialize(src, Options);
        var dst  = JsonSerializer.Deserialize<ForwardRequest>(json, Options);

        dst!.PromptVersionId.ShouldBe("v-abc");
        dst.ParentTraceId.ShouldBe("t-123");
        dst.Budget!.TokenLimit.ShouldBe(500L);
        dst.Inputs["question"].GetString().ShouldBe("hello");
    }

    [Fact]
    public void ForwardRequest_RoundTrip_OptionalFieldsNull()
    {
        var inputs = new Dictionary<string, JsonElement>
        {
            ["x"] = JsonDocument.Parse("1").RootElement,
        }.AsReadOnly();
        var src  = new ForwardRequest(Inputs: inputs);
        var json = JsonSerializer.Serialize(src, Options);
        json.ShouldNotContain("promptVersionId");
        json.ShouldNotContain("parentTraceId");
        json.ShouldNotContain("budget");
        var dst = JsonSerializer.Deserialize<ForwardRequest>(json, Options);
        dst!.PromptVersionId.ShouldBeNull();
        dst.ParentTraceId.ShouldBeNull();
        dst.Budget.ShouldBeNull();
    }

    // ──────────────────────── UsageInfo ────────────────────────

    [Fact]
    public void UsageInfo_RoundTrip()
    {
        var src  = new UsageInfo(PromptTokens: 100, CompletionTokens: 50, CostCents: 0.01m, DurationMs: 123.4);
        var json = JsonSerializer.Serialize(src, Options);
        var dst  = JsonSerializer.Deserialize<UsageInfo>(json, Options);
        dst.ShouldBe(src);
    }

    // ──────────────────────── ForwardResponse ────────────────────────

    [Fact]
    public void ForwardResponse_RoundTrip_AllFields()
    {
        var outputs = new Dictionary<string, JsonElement>
        {
            ["answer"] = JsonDocument.Parse("\"yes\"").RootElement,
        }.AsReadOnly();
        var usage = new UsageInfo(10, 5, 0.001m, 55.5);
        var src = new ForwardResponse(
            TraceId: "tr-1",
            Outputs: outputs,
            Usage: usage,
            ModelId: "gpt-4",
            ModuleName: "qa",
            PromptVersionId: "v1");
        var json = JsonSerializer.Serialize(src, Options);
        var dst  = JsonSerializer.Deserialize<ForwardResponse>(json, Options);
        dst!.TraceId.ShouldBe("tr-1");
        dst.Outputs["answer"].GetString().ShouldBe("yes");
        dst.Usage.ShouldBe(usage);
        dst.PromptVersionId.ShouldBe("v1");
    }

    [Fact]
    public void ForwardResponse_PromptVersionId_Nullable_OmittedWhenNull()
    {
        var outputs = new Dictionary<string, JsonElement>().AsReadOnly();
        var usage   = new UsageInfo(0, 0, 0m, 0);
        var src     = new ForwardResponse("t", outputs, usage, "m", "n", null);
        var json    = JsonSerializer.Serialize(src, Options);
        json.ShouldNotContain("promptVersionId");
    }
}

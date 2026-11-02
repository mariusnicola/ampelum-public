#pragma warning disable AMPELUM_V09_PREVIEW
using System.Text.Json;
using Shouldly;
using SignalGrid.Ampelum.Host.Contracts;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.Contracts;

public sealed class SsePayloadRoundTripTests
{
    private static readonly JsonSerializerOptions Options =
        AmpelumHostContractsJsonContext.Default.Options;

    [Fact]
    public void SseTokenPayload_RoundTrip()
    {
        var src = new SseTokenPayload(TraceId: "tr-1", Field: "Script", Delta: "Once ");
        var json = JsonSerializer.Serialize(src, Options);
        var dst = JsonSerializer.Deserialize<SseTokenPayload>(json, Options);
        dst!.TraceId.ShouldBe("tr-1");
        dst.Field.ShouldBe("Script");
        dst.Delta.ShouldBe("Once ");
    }

    [Fact]
    public void SsePartialPayload_RoundTrip()
    {
        var src = new SsePartialPayload(TraceId: "tr-2", Field: "Script", Value: "Once upon a time");
        var json = JsonSerializer.Serialize(src, Options);
        var dst = JsonSerializer.Deserialize<SsePartialPayload>(json, Options);
        dst!.TraceId.ShouldBe("tr-2");
        dst.Field.ShouldBe("Script");
        dst.Value.ShouldBe("Once upon a time");
    }

    [Fact]
    public void SseFinalPayload_RoundTrip()
    {
        var outputs = new Dictionary<string, JsonElement>
        {
            ["result"] = JsonDocument.Parse("\"success\"").RootElement,
        }.AsReadOnly();
        var usage = new UsageInfo(100, 50, 0.5m, 123.45);

        var src = new SseFinalPayload(
            TraceId: "tr-3",
            Outputs: outputs,
            Usage: usage,
            ModelId: "gpt-4",
            PromptVersionId: "v1");

        var json = JsonSerializer.Serialize(src, Options);
        var dst = JsonSerializer.Deserialize<SseFinalPayload>(json, Options);

        dst!.TraceId.ShouldBe("tr-3");
        dst.Outputs["result"].GetString().ShouldBe("success");
        dst.Usage.ShouldBe(usage);
        dst.ModelId.ShouldBe("gpt-4");
        dst.PromptVersionId.ShouldBe("v1");
    }

    [Fact]
    public void SseFinalPayload_PromptVersionId_Nullable()
    {
        var outputs = new Dictionary<string, JsonElement>().AsReadOnly();
        var usage = new UsageInfo(0, 0, 0m, 0);

        var src = new SseFinalPayload(
            TraceId: "tr-4",
            Outputs: outputs,
            Usage: usage,
            ModelId: "gpt-4",
            PromptVersionId: null);

        var json = JsonSerializer.Serialize(src, Options);
        json.ShouldNotContain("promptVersionId");
    }

    [Fact]
    public void SseErrorPayload_RoundTrip()
    {
        var src = new SseErrorPayload(TraceId: "tr-5", Code: "adapter_parse", Message: "Invalid JSON");
        var json = JsonSerializer.Serialize(src, Options);
        var dst = JsonSerializer.Deserialize<SseErrorPayload>(json, Options);
        dst!.TraceId.ShouldBe("tr-5");
        dst.Code.ShouldBe("adapter_parse");
        dst.Message.ShouldBe("Invalid JSON");
    }

    [Fact]
    public void SseDonePayload_RoundTrip()
    {
        var src = new SseDonePayload(TraceId: "tr-6");
        var json = JsonSerializer.Serialize(src, Options);
        var dst = JsonSerializer.Deserialize<SseDonePayload>(json, Options);
        dst!.TraceId.ShouldBe("tr-6");
    }

    [Fact]
    public void SseProgressPayload_RoundTrip()
    {
        var src = new SseProgressPayload(RunId: "run-1", TrialsCompleted: 5, TrialsTotal: 10);
        var json = JsonSerializer.Serialize(src, Options);
        var dst = JsonSerializer.Deserialize<SseProgressPayload>(json, Options);
        dst!.RunId.ShouldBe("run-1");
        dst.TrialsCompleted.ShouldBe(5);
        dst.TrialsTotal.ShouldBe(10);
    }

    [Fact]
    public void SseProgressPayload_TrialsTotal_Nullable()
    {
        var src = new SseProgressPayload(RunId: "run-2", TrialsCompleted: 3, TrialsTotal: null);
        var json = JsonSerializer.Serialize(src, Options);
        json.ShouldNotContain("trialsTotal");
    }

    [Fact]
    public void SseTrialPayload_RoundTrip()
    {
        var src = new SseTrialPayload(RunId: "run-3", TrialIndex: 2, Score: 0.83, Status: "succeeded");
        var json = JsonSerializer.Serialize(src, Options);
        var dst = JsonSerializer.Deserialize<SseTrialPayload>(json, Options);
        dst!.RunId.ShouldBe("run-3");
        dst.TrialIndex.ShouldBe(2);
        dst.Score.ShouldBe(0.83);
        dst.Status.ShouldBe("succeeded");
    }
}

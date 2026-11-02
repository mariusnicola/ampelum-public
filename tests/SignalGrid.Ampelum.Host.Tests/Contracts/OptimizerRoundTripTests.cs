#pragma warning disable AMPELUM_V09_PREVIEW
using System.Text.Json;
using Shouldly;
using SignalGrid.Ampelum.Host.Contracts;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.Contracts;

public sealed class OptimizerRoundTripTests
{
    private static readonly JsonSerializerOptions Options =
        AmpelumHostContractsJsonContext.Default.Options;

    private static DemoExample MakeExample() => new(
        new Dictionary<string, JsonElement>
        {
            ["q"] = JsonDocument.Parse("\"x\"").RootElement,
        }.AsReadOnly(),
        new Dictionary<string, JsonElement>
        {
            ["a"] = JsonDocument.Parse("\"y\"").RootElement,
        }.AsReadOnly());

    // ──────────────────────── ExampleSource polymorphism ────────────────────────

    [Fact]
    public void InlineExamples_RoundTrip_PreservesKindDiscriminator()
    {
        ExampleSource src  = new InlineExamples(new[] { MakeExample() });
        var json           = JsonSerializer.Serialize(src, Options);
        json.ShouldContain("\"kind\":\"inline\"");
        var dst = JsonSerializer.Deserialize<ExampleSource>(json, Options);
        dst.ShouldBeOfType<InlineExamples>()
           .Examples.Count.ShouldBe(1);
    }

    [Fact]
    public void FileExamples_RoundTrip_PreservesKindDiscriminator()
    {
        ExampleSource src = new FileExamples("/data/train.jsonl");
        var json          = JsonSerializer.Serialize(src, Options);
        json.ShouldContain("\"kind\":\"file\"");
        var dst = JsonSerializer.Deserialize<ExampleSource>(json, Options);
        dst.ShouldBeOfType<FileExamples>()
           .Path.ShouldBe("/data/train.jsonl");
    }

    [Fact]
    public void RemoteExamples_RoundTrip_WithSha()
    {
        ExampleSource src = new RemoteExamples("https://example.com/data.jsonl", "abc123");
        var json          = JsonSerializer.Serialize(src, Options);
        json.ShouldContain("\"kind\":\"remote\"");
        var dst = JsonSerializer.Deserialize<ExampleSource>(json, Options);
        var remote = dst.ShouldBeOfType<RemoteExamples>();
        remote.Uri.ShouldBe("https://example.com/data.jsonl");
        remote.Sha256.ShouldBe("abc123");
    }

    [Fact]
    public void RemoteExamples_Sha256_OmittedWhenNull()
    {
        ExampleSource src = new RemoteExamples("https://example.com/data.jsonl");
        var json          = JsonSerializer.Serialize(src, Options);
        json.ShouldNotContain("sha256");
    }

    // ──────────────────────── OptimizeRequest ────────────────────────

    [Fact]
    public void OptimizeRequest_RoundTrip_InlineSource()
    {
        var src = new OptimizeRequest(
            OptimizerType: OptimizerTypes.MIPROv2,
            TrainingSet: new InlineExamples(new[] { MakeExample() }),
            OptimizerOptions: new Dictionary<string, JsonElement>
            {
                ["maxBootstrapDemos"] = JsonDocument.Parse("4").RootElement,
            }.AsReadOnly(),
            Budget: new BudgetHint(CallLimit: 20));
        var json = JsonSerializer.Serialize(src, Options);
        var dst  = JsonSerializer.Deserialize<OptimizeRequest>(json, Options);
        dst!.OptimizerType.ShouldBe(OptimizerTypes.MIPROv2);
        dst.TrainingSet.ShouldBeOfType<InlineExamples>();
        dst.OptimizerOptions["maxBootstrapDemos"].GetInt32().ShouldBe(4);
        dst.Budget!.CallLimit.ShouldBe(20);
    }

    [Fact]
    public void OptimizeRequest_Budget_OmittedWhenNull()
    {
        var src = new OptimizeRequest(
            OptimizerTypes.BootstrapFewShot,
            new FileExamples("/d/t.jsonl"),
            new Dictionary<string, JsonElement>().AsReadOnly());
        var json = JsonSerializer.Serialize(src, Options);
        json.ShouldNotContain("budget");
    }

    // ──────────────────────── OptimizeRunHandle ────────────────────────

    [Fact]
    public void OptimizeRunHandle_RoundTrip()
    {
        var src  = new OptimizeRunHandle("run-123", "qa");
        var json = JsonSerializer.Serialize(src, Options);
        var dst  = JsonSerializer.Deserialize<OptimizeRunHandle>(json, Options);
        dst!.RunId.ShouldBe("run-123");
        dst.ModuleName.ShouldBe("qa");
    }

    // ──────────────────────── OptimizeRunStatus ────────────────────────

    [Fact]
    public void OptimizeRunStatus_RoundTrip_AllFields()
    {
        var start = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var end   = start.AddMinutes(5);
        var src = new OptimizeRunStatus(
            RunId: "r1",
            ModuleName: "qa",
            OptimizerType: OptimizerTypes.COPRO,
            State: OptimizeRunState.Succeeded,
            StartedAt: start,
            CompletedAt: end,
            TrialsCompleted: 10,
            TrialsTotal: 10,
            ResultPromptVersionId: "v2",
            Error: null);
        var json = JsonSerializer.Serialize(src, Options);
        var dst  = JsonSerializer.Deserialize<OptimizeRunStatus>(json, Options);
        dst!.State.ShouldBe(OptimizeRunState.Succeeded);
        dst.TrialsCompleted.ShouldBe(10);
        dst.ResultPromptVersionId.ShouldBe("v2");
        dst.Error.ShouldBeNull();
    }

    [Fact]
    public void OptimizeRunStatus_NullableFields_OmittedWhenNull()
    {
        var src = new OptimizeRunStatus(
            "r", "m", OptimizerTypes.SIMBA, OptimizeRunState.Running,
            DateTimeOffset.UtcNow, null, 0, null, null, null);
        var json = JsonSerializer.Serialize(src, Options);
        json.ShouldNotContain("completedAt");
        json.ShouldNotContain("trialsTotal");
        json.ShouldNotContain("resultPromptVersionId");
        json.ShouldNotContain("error");
    }
}

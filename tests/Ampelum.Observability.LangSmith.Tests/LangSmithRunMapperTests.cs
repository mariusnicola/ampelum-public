using Xunit;
using SignalGrid.Ampelum.Observability.LangSmith;
using Shouldly;

namespace Ampelum.Observability.LangSmith.Tests;

public sealed class LangSmithRunMapperTests
{
    [Fact]
    public void Map_PopulatesNameRunTypeAndTimestamps()
    {
        var t = new FakeTimeProvider(DateTimeOffset.Parse("2026-04-25T12:00:00Z"));
        var mapper = new LangSmithRunMapper(t);
        var record = TraceRecordBuilder.Sample();
        var run = mapper.Map(record, "p1", includeAuditPayload: false);

        run.Id.ShouldBe(record.Id);
        run.Name.ShouldBe("Predict");
        run.RunType.ShouldBe("llm");
        run.SessionName.ShouldBe("p1");
        run.EndTime.ShouldBe(t.Now);
        run.StartTime.ShouldBe(t.Now - record.Duration);
        run.Inputs.ShouldBeNull();
        run.Outputs.ShouldBeNull();
    }

    [Theory]
    [InlineData("Predict", "llm")]
    [InlineData("ChainOfThought", "chain")]
    [InlineData("ReAct", "tool")]
    [InlineData("Retriever", "retriever")]
    [InlineData("EmbeddingGenerator", "embedding")]
    [InlineData("WhoKnows", "chain")]
    public void Map_ResolvesRunTypeFromModuleName(string module, string expected)
    {
        var mapper = new LangSmithRunMapper();
        var run = mapper.Map(TraceRecordBuilder.Sample(moduleName: module), "p", false);
        run.RunType.ShouldBe(expected);
    }

    [Fact]
    public void Map_PopulatesTokenUsageInExtraRuntime()
    {
        var mapper = new LangSmithRunMapper();
        var run = mapper.Map(TraceRecordBuilder.Sample(), "p", false);
        var runtime = (IReadOnlyDictionary<string, object?>)run.Extra!["runtime"]!;
        var usage = (IReadOnlyDictionary<string, object?>)runtime["token_usage"]!;
        usage["input_tokens"].ShouldBe(12L);
        usage["output_tokens"].ShouldBe(3L);
        usage["total_tokens"].ShouldBe(15L);
    }

    [Fact]
    public void Map_OmitsTokenUsageWhenAbsent()
    {
        var mapper = new LangSmithRunMapper();
        var run = mapper.Map(TraceRecordBuilder.Sample(withUsage: false), "p", false);
        var runtime = (IReadOnlyDictionary<string, object?>)run.Extra!["runtime"]!;
        runtime.ShouldNotContainKey("token_usage");
    }

    [Fact]
    public void Map_FlowsErrorMessageWhenForwardThrew()
    {
        var mapper = new LangSmithRunMapper();
        var run = mapper.Map(TraceRecordBuilder.Sample(error: true), "p", true);
        run.Error.ShouldBe("boom");
        run.Outputs.ShouldBeNull(); // suppressed on error even with IncludeAuditPayload=true
    }

    [Fact]
    public void Map_IncludesPayloadWhenOptedInAndNoError()
    {
        var mapper = new LangSmithRunMapper();
        var run = mapper.Map(TraceRecordBuilder.Sample(), "p", true);
        run.Inputs.ShouldNotBeNull();
        run.Outputs.ShouldNotBeNull();
        run.Outputs!["answer"].ShouldBe("4");
    }

    [Fact]
    public void Map_ProjectsMetadataUnderAmpelumPrefix()
    {
        var mapper = new LangSmithRunMapper();
        var meta = new Dictionary<string, object?> { ["reasoning"] = "step-by-step" };
        var run = mapper.Map(TraceRecordBuilder.Sample(metadata: meta), "p", false);
        var metadata = (IReadOnlyDictionary<string, object?>)run.Extra!["metadata"]!;
        metadata["ampelum.module"].ShouldBe("Predict");
        metadata["ampelum.reasoning"].ShouldBe("step-by-step");
        metadata.ShouldContainKey("ampelum.signature_type");
        metadata.ShouldContainKey("ampelum.trace_id");
    }

    [Fact]
    public void Map_ThrowsOnNullRecord()
    {
        var mapper = new LangSmithRunMapper();
        Should.Throw<ArgumentNullException>(() => mapper.Map(null!, "p", false));
    }

    [Fact]
    public void Map_ThrowsOnEmptyProject()
    {
        var mapper = new LangSmithRunMapper();
        Should.Throw<ArgumentException>(() => mapper.Map(TraceRecordBuilder.Sample(), "", false));
    }

    sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

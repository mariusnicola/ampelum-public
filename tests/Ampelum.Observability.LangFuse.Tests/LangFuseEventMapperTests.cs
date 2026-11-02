using Xunit;
using SignalGrid.Ampelum.Observability.LangFuse;
using Shouldly;

namespace Ampelum.Observability.LangFuse.Tests;

public sealed class LangFuseEventMapperTests
{
    sealed class FakeTime(DateTimeOffset n) : TimeProvider { public override DateTimeOffset GetUtcNow() => n; }

    [Fact]
    public void Map_EmitsTraceAndGenerationEvents()
    {
        var t = new FakeTime(DateTimeOffset.Parse("2026-04-25T12:00:00Z"));
        var mapper = new LangFuseEventMapper(t);
        var events = mapper.Map(TraceRecordBuilder.Sample(), false);
        events.Count.ShouldBe(2);
        events[0].Type.ShouldBe("trace-create");
        events[1].Type.ShouldBe("generation-create");
    }

    [Fact]
    public void Map_AssignsUniqueEventIdsAndSharedTraceId()
    {
        var mapper = new LangFuseEventMapper();
        var rec = TraceRecordBuilder.Sample();
        var events = mapper.Map(rec, false);
        events[0].Id.ShouldNotBe(events[1].Id);
        var trace = (LangFuseTraceBody)events[0].Body;
        var gen = (LangFuseGenerationBody)events[1].Body;
        trace.Id.ShouldBe(gen.TraceId);
        trace.Id.ShouldBe(rec.Id.ToString("N"));
    }

    [Fact]
    public void Map_PopulatesUsageWhenPresent()
    {
        var events = new LangFuseEventMapper().Map(TraceRecordBuilder.Sample(), false);
        var gen = (LangFuseGenerationBody)events[1].Body;
        gen.Usage.ShouldNotBeNull();
        gen.Usage!.Input.ShouldBe(5);
        gen.Usage.Output.ShouldBe(7);
        gen.Usage.Total.ShouldBe(12);
        gen.Usage.Unit.ShouldBe("TOKENS");
    }

    [Fact]
    public void Map_OmitsUsageWhenAbsent()
    {
        var events = new LangFuseEventMapper().Map(TraceRecordBuilder.Sample(withUsage: false), false);
        var gen = (LangFuseGenerationBody)events[1].Body;
        gen.Usage.ShouldBeNull();
    }

    [Fact]
    public void Map_FlowsErrorIntoStatusAndLevel()
    {
        var events = new LangFuseEventMapper().Map(TraceRecordBuilder.Sample(error: true), true);
        var gen = (LangFuseGenerationBody)events[1].Body;
        gen.Level.ShouldBe("ERROR");
        gen.StatusMessage.ShouldBe("oops");
        gen.Output.ShouldBeNull();
    }

    [Fact]
    public void Map_IncludesPayloadWhenOptedIn()
    {
        var events = new LangFuseEventMapper().Map(TraceRecordBuilder.Sample(), true);
        var gen = (LangFuseGenerationBody)events[1].Body;
        gen.Input.ShouldNotBeNull();
        gen.Output.ShouldNotBeNull();
    }

    [Fact]
    public void Map_OmitsPayloadByDefault()
    {
        var events = new LangFuseEventMapper().Map(TraceRecordBuilder.Sample(), false);
        var gen = (LangFuseGenerationBody)events[1].Body;
        gen.Input.ShouldBeNull();
        gen.Output.ShouldBeNull();
    }

    [Fact]
    public void Map_ProjectsMetadataUnderAmpelumPrefix()
    {
        var meta = new Dictionary<string, object?> { ["reasoning"] = "step" };
        var events = new LangFuseEventMapper().Map(TraceRecordBuilder.Sample(metadata: meta), false);
        var trace = (LangFuseTraceBody)events[0].Body;
        trace.Metadata!["ampelum.module"].ShouldBe("Predict");
        trace.Metadata["ampelum.reasoning"].ShouldBe("step");
    }

    [Fact]
    public void Map_ThrowsOnNullRecord()
    {
        Should.Throw<ArgumentNullException>(() => new LangFuseEventMapper().Map(null!, false));
    }
}

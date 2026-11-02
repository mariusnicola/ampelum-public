using Xunit;
using System.Diagnostics;
using SignalGrid.Ampelum.Observability.Phoenix;
using Shouldly;

namespace Ampelum.Observability.Phoenix.Tests;

public sealed class PhoenixSpanMapperTests
{
    static Activity NewActivity()
    {
        var src = new ActivitySource("phoenix-mapper-tests");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        var act = src.StartActivity("test", ActivityKind.Internal)!;
        return act;
    }

    [Fact]
    public void Apply_SetsAmpelumModuleAndTraceId()
    {
        var a = NewActivity();
        var rec = TraceRecordBuilder.Sample();
        new PhoenixSpanMapper().Apply(a, rec, new PhoenixExporterOptions());
        a.GetTagItem("ampelum.module").ShouldBe("Predict");
        a.GetTagItem("ampelum.trace_id").ShouldBe(rec.Id.ToString("D"));
        a.GetTagItem("ampelum.signature_type").ShouldBe("TestSig");
        a.Stop();
    }

    [Fact]
    public void Apply_EmitsOpenInferenceAliasesByDefault()
    {
        var a = NewActivity();
        new PhoenixSpanMapper().Apply(a, TraceRecordBuilder.Sample(), new PhoenixExporterOptions());
        a.GetTagItem("gen_ai.usage.input_tokens").ShouldBe(4L);
        a.GetTagItem("llm.token_count.prompt").ShouldBe(4L);
        a.GetTagItem("gen_ai.usage.output_tokens").ShouldBe(6L);
        a.GetTagItem("llm.token_count.completion").ShouldBe(6L);
        a.GetTagItem("llm.token_count.total").ShouldBe(10L);
        a.GetTagItem("openinference.span.kind").ShouldBe("LLM");
        a.Stop();
    }

    [Fact]
    public void Apply_OmitsAliasesWhenDisabled()
    {
        var a = NewActivity();
        new PhoenixSpanMapper().Apply(a, TraceRecordBuilder.Sample(),
            new PhoenixExporterOptions { EmitOpenInferenceAliases = false });
        a.GetTagItem("llm.token_count.prompt").ShouldBeNull();
        a.GetTagItem("openinference.span.kind").ShouldBeNull();
        a.GetTagItem("gen_ai.usage.input_tokens").ShouldBe(4L);
        a.Stop();
    }

    [Fact]
    public void Apply_FlowsErrorAsStatusAndKind()
    {
        var a = NewActivity();
        new PhoenixSpanMapper().Apply(a, TraceRecordBuilder.Sample(error: true), new PhoenixExporterOptions());
        a.Status.ShouldBe(ActivityStatusCode.Error);
        a.GetTagItem("exception.type").ShouldBe(typeof(InvalidOperationException).FullName);
        a.GetTagItem("exception.message").ShouldBe("nope");
        a.GetTagItem("openinference.span.kind").ShouldBe("ERROR");
        a.Stop();
    }

    [Fact]
    public void Apply_OmitsUsageWhenAbsent()
    {
        var a = NewActivity();
        new PhoenixSpanMapper().Apply(a, TraceRecordBuilder.Sample(withUsage: false), new PhoenixExporterOptions());
        a.GetTagItem("gen_ai.usage.input_tokens").ShouldBeNull();
        a.GetTagItem("llm.token_count.prompt").ShouldBeNull();
        a.Stop();
    }

    [Fact]
    public void Apply_EmitsAuditEventsWhenIncludeAuditPayload()
    {
        var a = NewActivity();
        new PhoenixSpanMapper().Apply(a, TraceRecordBuilder.Sample(),
            new PhoenixExporterOptions { IncludeAuditPayload = true });
        var events = a.Events.ToList();
        events.ShouldContain(e => e.Name == "gen_ai.event.user.message");
        events.ShouldContain(e => e.Name == "gen_ai.event.assistant.message");
        a.Stop();
    }

    [Fact]
    public void Apply_ProjectsMetadataUnderAmpelumPrefix()
    {
        var a = NewActivity();
        var meta = new Dictionary<string, object?> { ["reasoning"] = "step" };
        new PhoenixSpanMapper().Apply(a, TraceRecordBuilder.Sample(metadata: meta), new PhoenixExporterOptions());
        a.GetTagItem("ampelum.reasoning").ShouldBe("step");
        a.Stop();
    }

    [Fact]
    public void Apply_ValidatesArguments()
    {
        var m = new PhoenixSpanMapper();
        var a = NewActivity();
        var opts = new PhoenixExporterOptions();
        var rec = TraceRecordBuilder.Sample();
        Should.Throw<ArgumentNullException>(() => m.Apply(null!, rec, opts));
        Should.Throw<ArgumentNullException>(() => m.Apply(a, null!, opts));
        Should.Throw<ArgumentNullException>(() => m.Apply(a, rec, null!));
        a.Stop();
    }
}

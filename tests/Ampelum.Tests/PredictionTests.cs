using SignalGrid.Ampelum;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

public sealed class PredictionTests
{
    static TraceRecord Make(TimeSpan? d = null, UsageDetails? u = null) =>
        new(Id: Guid.NewGuid(),
            ModuleName: "Predict",
            SignatureType: typeof(object),
            Inputs: new Dictionary<string, object?>(),
            Outputs: new Dictionary<string, object?>(),
            PromptMessages: [],
            RawResponse: "",
            Usage: u,
            Duration: d ?? TimeSpan.FromMilliseconds(1),
            Error: null,
            Metadata: new Dictionary<string, object?>());

    [Fact]
    public void Prediction_HoldsValueAndTrace()
    {
        var t = Make();
        var p = new Prediction<string>("hi", t);
        p.Value.ShouldBe("hi");
        p.Trace.ShouldBeSameAs(t);
    }

    [Fact]
    public void Duration_Ext_ReturnsTraceDuration()
    {
        var t = Make(d: TimeSpan.FromSeconds(2));
        new Prediction<int>(0, t).Duration().ShouldBe(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Usage_Ext_ReturnsTraceUsage()
    {
        var u = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 20 };
        var t = Make(u: u);
        var p = new Prediction<int>(0, t);
        p.Usage().ShouldBeSameAs(u);
    }

    [Fact]
    public void Usage_Ext_NullWhenAbsent()
    {
        new Prediction<int>(0, Make()).Usage().ShouldBeNull();
    }

    [Fact]
    public void Reasoning_Ext_ReadsFromOutputs()
    {
        var t = Make() with { Outputs = new Dictionary<string, object?> { ["reasoning"] = "because" } };
        new Prediction<int>(0, t).Reasoning().ShouldBe("because");
    }

    [Fact]
    public void Reasoning_Ext_EmptyStringWhenMissing()
    {
        new Prediction<int>(0, Make()).Reasoning().ShouldBe("");
    }

    [Fact]
    public void Reasoning_Ext_NullValueGivesEmptyString()
    {
        var t = Make() with { Outputs = new Dictionary<string, object?> { ["reasoning"] = null } };
        new Prediction<int>(0, t).Reasoning().ShouldBe("");
    }

    [Fact]
    public void TraceRecord_With_CopiesAndOverrides()
    {
        var r1 = new TraceRecord(
            Id: Guid.NewGuid(), ModuleName: "M", SignatureType: typeof(object),
            Inputs: new Dictionary<string, object?>(), Outputs: new Dictionary<string, object?>(),
            PromptMessages: [], RawResponse: "r1", Usage: null, Duration: TimeSpan.Zero,
            Error: null, Metadata: new Dictionary<string, object?>());
        var r2 = r1 with { RawResponse = "r2" };
        r2.RawResponse.ShouldBe("r2");
        r1.RawResponse.ShouldBe("r1");
        r1.Id.ShouldBe(r2.Id);
        // Exercise PromptMessages property getter (TraceRecord line 11)
        r1.PromptMessages.Count.ShouldBe(0);
        r2.PromptMessages.Count.ShouldBe(0);
    }
}

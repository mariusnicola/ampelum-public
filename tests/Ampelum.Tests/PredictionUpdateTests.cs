using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

public sealed class PredictionUpdateTests
{
    [Signature("")] public sealed record S : ISignature
    {
        [Input]  public required string A { get; init; }
        [Output] public string B { get; init; } = "";
    }

    [Fact]
    public void NonFinal_HasNullValueAndTrace()
    {
        var u = new PredictionUpdate<S>("B", "par", IsFieldComplete: false, IsFinal: false, Value: null, Trace: null);
        u.FieldName.ShouldBe("B");
        u.PartialText.ShouldBe("par");
        u.Value.ShouldBeNull();
        u.Trace.ShouldBeNull();
    }

    [Fact]
    public void Final_CarriesValueAndTrace()
    {
        var s = new S { A = "?", B = "done" };
        var trace = new TraceRecord(Guid.NewGuid(), "Predict", typeof(S),
            new Dictionary<string, object?>(), new Dictionary<string, object?>(),
            Array.Empty<Microsoft.Extensions.AI.ChatMessage>(), "", null, TimeSpan.Zero, null,
            new Dictionary<string, object?>());
        var u = new PredictionUpdate<S>("B", "done", IsFieldComplete: true, IsFinal: true, Value: s, Trace: trace);
        u.IsFinal.ShouldBeTrue();
        u.Value!.B.ShouldBe("done");
        u.Trace.ShouldBeSameAs(trace);
    }

    [Fact]
    public void IsFieldComplete_IsStored()
    {
        var incomplete = new PredictionUpdate<S>("B", "par", IsFieldComplete: false, IsFinal: false, Value: null, Trace: null);
        incomplete.IsFieldComplete.ShouldBeFalse();
        var complete = new PredictionUpdate<S>("B", "par", IsFieldComplete: true, IsFinal: true, Value: null, Trace: null);
        complete.IsFieldComplete.ShouldBeTrue();
    }
}

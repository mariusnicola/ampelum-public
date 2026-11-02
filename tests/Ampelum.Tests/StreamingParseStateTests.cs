using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

public sealed class StreamingParseStateTests
{
    [Fact] public void MaxPartialMarkerLength_Is64() =>
        StreamingParseState.MaxPartialMarkerLength.ShouldBe(64);

    [Fact]
    public void Construct_AllFieldsStored()
    {
        var completed = new Dictionary<string, object?> { ["a"] = "x" };
        var s = new StreamingParseState(
            CurrentFieldName: "b",
            AccumulatedText: "acc",
            PendingTail: "[[",
            CompletedFields: completed,
            IsComplete: false);
        s.CurrentFieldName.ShouldBe("b");
        s.AccumulatedText.ShouldBe("acc");
        s.PendingTail.ShouldBe("[[");
        s.CompletedFields["a"].ShouldBe("x");
        s.IsComplete.ShouldBeFalse();
    }

    [Fact]
    public void IStreamingAdapter_ExtendsIAdapter() =>
        typeof(IAdapter).IsAssignableFrom(typeof(IStreamingAdapter)).ShouldBeTrue();

    [Fact]
    public void Records_AreEqual_ByValue()
    {
        var a = new StreamingParseState("f", "t", "p", new Dictionary<string, object?>(), false);
        var b = new StreamingParseState("f", "t", "p", a.CompletedFields, false);
        a.ShouldBe(b);                // uses generated Equals
        a.GetHashCode().ShouldBe(b.GetHashCode());
        a.ToString().ShouldContain("StreamingParseState");  // uses generated PrintMembers
    }

    [Fact]
    public void With_Expression_ProducesModifiedCopy()
    {
        var a = new StreamingParseState("f", "t", "p", new Dictionary<string, object?>(), false);
        var b = a with { IsComplete = true };  // uses generated <Clone>$
        b.IsComplete.ShouldBeTrue();
        a.IsComplete.ShouldBeFalse();
    }
}

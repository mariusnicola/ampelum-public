using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

public sealed class ToolCallRecordTests
{
    [Fact]
    public void Success_SetsResultAndLeavesErrorNull()
    {
        var rec = ToolCallRecord.Success(
            "search",
            new Dictionary<string, object?> { ["q"] = "dotnet" },
            result: "ok",
            duration: TimeSpan.FromMilliseconds(12));

        rec.FunctionName.ShouldBe("search");
        rec.Arguments["q"].ShouldBe("dotnet");
        rec.Result.ShouldBe("ok");
        rec.Error.ShouldBeNull();
        rec.Duration.ShouldBe(TimeSpan.FromMilliseconds(12));
    }

    [Fact]
    public void Failed_SetsErrorAndLeavesResultNull()
    {
        var ex = new InvalidOperationException("boom");
        var rec = ToolCallRecord.Failed(
            "search",
            new Dictionary<string, object?>(),
            ex,
            TimeSpan.FromMilliseconds(5));

        rec.Result.ShouldBeNull();
        rec.Error.ShouldBeSameAs(ex);
    }
}

using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

public sealed class TraceKeysDemosUsedTests
{
    [Fact]
    public void DemosUsed_HasExpectedValue() =>
        TraceKeys.DemosUsed.ShouldBe("demos_used");
}

using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

public sealed class TraceKeysCitationsTests
{
    [Fact]
    public void Citations_HasExpectedValue() =>
        TraceKeys.Citations.ShouldBe("citations");
}

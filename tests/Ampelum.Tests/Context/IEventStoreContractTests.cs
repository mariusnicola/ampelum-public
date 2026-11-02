using SignalGrid.Ampelum.Context;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Context;

public class IEventStoreContractTests
{
    [Fact]
    public void EventQuery_DefaultsAreNullAndNonInclusive()
    {
        var q = new EventQuery();
        q.SinceTurn.ShouldBeNull();
        q.UntilTurn.ShouldBeNull();
        q.Types.ShouldBeNull();
        q.IncludeSuperseded.ShouldBeFalse();
    }

    [Fact]
    public void EventTooLargeException_CarriesByteSize()
    {
        var ex = new EventTooLargeException(actualBytes: 300_000, maxBytes: 262_144);
        ex.ActualBytes.ShouldBe(300_000);
        ex.MaxBytes.ShouldBe(262_144);
        ex.Message.ShouldContain("300000");
    }
}

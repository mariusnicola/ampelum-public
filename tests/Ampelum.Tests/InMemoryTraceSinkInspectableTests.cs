using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Usage;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

namespace Ampelum.Tests;

public sealed class InMemoryTraceSinkInspectableTests
{
    static TraceRecord Rec(string id) => new(Guid.NewGuid(), id, typeof(object),
        new Dictionary<string, object?>(), new Dictionary<string, object?>(),
        Array.Empty<ChatMessage>(), "", null, TimeSpan.Zero, null,
        new Dictionary<string, object?>());

    [Fact]
    public void GetRecent_ReturnsLastN_InChronologicalOrder()
    {
        var s = new InMemoryTraceSink();
        IInspectableSink inspectable = s;
        s.Write(Rec("a"));
        s.Write(Rec("b"));
        s.Write(Rec("c"));
        var r = inspectable.GetRecent(2);
        r.Count.ShouldBe(2);
        r[0].ModuleName.ShouldBe("b");
        r[1].ModuleName.ShouldBe("c");
    }

    [Fact]
    public void GetRecent_ZeroOrNegative_ReturnsEmpty()
    {
        var s = new InMemoryTraceSink();
        s.Write(Rec("a"));
        ((IInspectableSink)s).GetRecent(0).ShouldBeEmpty();
        ((IInspectableSink)s).GetRecent(-1).ShouldBeEmpty();
    }

    [Fact]
    public void GetRecent_N_GT_Available_ReturnsAll()
    {
        var s = new InMemoryTraceSink();
        s.Write(Rec("a"));
        ((IInspectableSink)s).GetRecent(99).Count.ShouldBe(1);
    }
}

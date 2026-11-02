using SignalGrid.Ampelum.Context;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Context;

public class ContentHasherTests
{
    [Fact]
    public void Hash_ProducesLowercaseHex64Chars()
    {
        var h = ContentHasher.HashHex("hello");
        h.Length.ShouldBe(64);
        h.ShouldBe(h.ToLowerInvariant());
    }

    [Fact]
    public void Hash_IsDeterministic()
        => ContentHasher.HashHex("abc").ShouldBe(ContentHasher.HashHex("abc"));

    [Fact]
    public void EventKey_FormatIsTypeColonTurnColonSeqColonHashPrefix()
    {
        var k = EventKey.Make("user_msg", turn: 5, seq: 2, payload: "hello");
        k.ShouldStartWith("user_msg:5:2:");
        k.Length.ShouldBe("user_msg:5:2:".Length + 8);
    }
}

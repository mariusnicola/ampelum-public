using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Exceptions;
using Shouldly;
using Xunit;

public sealed class AmpelumAssertTests
{
    [Fact]
    public void Assert_False_Throws() =>
        Should.Throw<AssertionFailedException>(() => AmpelumAssert.Assert(false, "bad")).Message.ShouldBe("bad");

    [Fact]
    public void Assert_True_DoesNotThrow() =>
        AmpelumAssert.Assert(true, "ok");

    [Fact]
    public void Suggest_False_Throws() =>
        Should.Throw<SuggestionException>(() => AmpelumAssert.Suggest(false, "hint")).Suggestion.ShouldBe("hint");

    [Fact]
    public void Suggest_True_DoesNotThrow() =>
        AmpelumAssert.Suggest(true, "hint");
}

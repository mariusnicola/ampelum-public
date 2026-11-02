using SignalGrid.Ampelum.Exceptions;
using Shouldly;
using Xunit;

public sealed class SuggestionExceptionTests
{
    [Fact]
    public void Ctor_SetsSuggestion_AndMessage()
    {
        var ex = new SuggestionException("please explain");
        ex.Suggestion.ShouldBe("please explain");
        ex.Message.ShouldBe("please explain");
    }

    [Fact]
    public void IsAmpelumException() =>
        (new SuggestionException("x") is AmpelumException).ShouldBeTrue();
}

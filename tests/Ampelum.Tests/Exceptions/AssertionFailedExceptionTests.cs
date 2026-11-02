using SignalGrid.Ampelum.Exceptions;
using Shouldly;
using Xunit;

public sealed class AssertionFailedExceptionTests
{
    [Fact]
    public void Ctor_SetsMessage() =>
        new AssertionFailedException("x").Message.ShouldBe("x");

    [Fact]
    public void IsAmpelumException() =>
        (new AssertionFailedException("x") is AmpelumException).ShouldBeTrue();
}

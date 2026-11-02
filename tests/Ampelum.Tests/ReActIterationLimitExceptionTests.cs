using SignalGrid.Ampelum.Exceptions;
using Shouldly;
using Xunit;

public sealed class ReActIterationLimitExceptionTests
{
    [Fact]
    public void Properties_AreExposed()
    {
        var ex = new ReActIterationLimitException(5, "hit limit");
        ex.MaxIterations.ShouldBe(5);
        ex.Message.ShouldBe("hit limit");
        ex.ShouldBeAssignableTo<AmpelumException>();
    }
}

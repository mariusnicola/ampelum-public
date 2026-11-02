using SignalGrid.Ampelum.Persistence;
using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Persistence;

public sealed class ProgramPersistenceExceptionTests
{
    [Fact]
    public void Ctor_MessageOnly_SetsMessage()
    {
        var ex = new ProgramPersistenceException("boom");
        ex.Message.ShouldBe("boom");
        ex.InnerException.ShouldBeNull();
    }

    [Fact]
    public void Ctor_MessageAndInner_SetsBoth()
    {
        var inner = new InvalidOperationException("inner");
        var ex = new ProgramPersistenceException("outer", inner);
        ex.Message.ShouldBe("outer");
        ex.InnerException.ShouldBe(inner);
    }

    [Fact]
    public void IsSubclassOf_AmpelumException() =>
        new ProgramPersistenceException("x")
            .ShouldBeAssignableTo<SignalGrid.Ampelum.Exceptions.AmpelumException>();
}

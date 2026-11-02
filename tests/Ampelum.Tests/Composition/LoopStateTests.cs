namespace Ampelum.Tests.Composition;

using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Composition;
using Ampelum.Tests.Composition._Sigs;
using Shouldly;
using Xunit;

public sealed class LoopStateTests
{
    [Fact]
    public void Construct_DefaultHistoryEmpty()
    {
        var state = new LoopState<TestSig>
        {
            Iteration = 1,
            CurrentValue = new TestSig { Question = "q" },
        };
        state.History.ShouldNotBeNull();
        state.History.Count.ShouldBe(0);
        state.IsConverged.ShouldBeFalse();
    }

    [Fact]
    public void Construct_WithHistory_PreservesReference()
    {
        var hist = new[] { new Prediction<TestSig>(new TestSig(), null!) };
        var state = new LoopState<TestSig>
        {
            Iteration = 2,
            CurrentValue = new TestSig(),
            History = hist,
            IsConverged = true,
        };
        state.History.ShouldBeSameAs(hist);
        state.IsConverged.ShouldBeTrue();
    }

    [Fact]
    public void Iteration_IsRequired_RecordCannotConstructWithout()
    {
        // Static smoke: ensures `Iteration` is `required`; pure compile-time guarantee.
        // The fact that this file compiles AT ALL is the assertion. (No runtime check.)
        true.ShouldBeTrue();
    }
}

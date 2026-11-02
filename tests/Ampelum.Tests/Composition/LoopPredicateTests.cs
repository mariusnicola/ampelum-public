namespace Ampelum.Tests.Composition;

using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Composition;
using Ampelum.Tests.Composition._Sigs;
using Shouldly;
using Xunit;

public sealed class LoopPredicateTests
{
    static LoopState<ScoreSig> StateAt(int iter, decimal score, IReadOnlyList<Prediction<ScoreSig>>? history = null) =>
        new()
        {
            Iteration = iter,
            CurrentValue = new ScoreSig { Score = score },
            History = history ?? Array.Empty<Prediction<ScoreSig>>(),
        };

    // ---------- MaxIterations ----------

    [Fact]
    public void MaxIterations_ShouldStop_WhenIterationGteMax()
    {
        var p = new MaxIterations<ScoreSig>(3);
        p.ShouldStop(StateAt(2, 0)).ShouldBeFalse();
        p.ShouldStop(StateAt(3, 0)).ShouldBeTrue();
        p.ShouldStop(StateAt(4, 0)).ShouldBeTrue();
    }

    [Fact]
    public void MaxIterations_ParametersExposeMax()
    {
        var p = new MaxIterations<ScoreSig>(7);
        p.Parameters["max"].ShouldBe(7);
    }

    [Fact]
    public void MaxIterations_WithParameters_ReturnsNewInstanceWithUpdatedMax()
    {
        var p = new MaxIterations<ScoreSig>(3);
        var p2 = p.WithParameters(new Dictionary<string, object?> { ["max"] = 9 });
        p2.ShouldNotBeSameAs(p);
        p2.Parameters["max"].ShouldBe(9);
    }

    [Fact]
    public void MaxIterations_CtorRejectsNonPositive()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new MaxIterations<ScoreSig>(0));
        Should.Throw<ArgumentOutOfRangeException>(() => new MaxIterations<ScoreSig>(-1));
    }

    // ---------- FieldThreshold ----------

    [Fact]
    public void FieldThreshold_LessThan_StopsWhenBelowThreshold()
    {
        var p = new FieldThreshold<ScoreSig>("Score", 0.5m, ThresholdDirection.LessThan);
        p.ShouldStop(StateAt(1, 0.4m)).ShouldBeTrue();
        p.ShouldStop(StateAt(1, 0.5m)).ShouldBeFalse();
        p.ShouldStop(StateAt(1, 0.6m)).ShouldBeFalse();
    }

    [Fact]
    public void FieldThreshold_LessThanOrEqual_StopsWhenAtOrBelowThreshold()
    {
        var p = new FieldThreshold<ScoreSig>("Score", 0.5m, ThresholdDirection.LessThanOrEqual);
        p.ShouldStop(StateAt(1, 0.5m)).ShouldBeTrue();
        p.ShouldStop(StateAt(1, 0.6m)).ShouldBeFalse();
    }

    [Fact]
    public void FieldThreshold_GreaterThan_StopsWhenAboveThreshold()
    {
        var p = new FieldThreshold<ScoreSig>("Score", 0.5m, ThresholdDirection.GreaterThan);
        p.ShouldStop(StateAt(1, 0.6m)).ShouldBeTrue();
        p.ShouldStop(StateAt(1, 0.5m)).ShouldBeFalse();
    }

    [Fact]
    public void FieldThreshold_GreaterThanOrEqual_StopsWhenAtOrAboveThreshold()
    {
        var p = new FieldThreshold<ScoreSig>("Score", 0.5m, ThresholdDirection.GreaterThanOrEqual);
        p.ShouldStop(StateAt(1, 0.5m)).ShouldBeTrue();
        p.ShouldStop(StateAt(1, 0.4m)).ShouldBeFalse();
    }

    [Fact]
    public void FieldThreshold_ParametersExposeFieldThresholdDirection()
    {
        var p = new FieldThreshold<ScoreSig>("Score", 0.42m, ThresholdDirection.LessThan);
        p.Parameters["field"].ShouldBe("Score");
        p.Parameters["threshold"].ShouldBe(0.42m);
        p.Parameters["direction"].ShouldBe(ThresholdDirection.LessThan);
    }

    [Fact]
    public void FieldThreshold_WithParameters_RebindsThresholdAndDirection()
    {
        var p = new FieldThreshold<ScoreSig>("Score", 0.5m, ThresholdDirection.LessThan);
        var p2 = p.WithParameters(new Dictionary<string, object?>
        {
            ["field"] = "Score",
            ["threshold"] = 0.9m,
            ["direction"] = ThresholdDirection.GreaterThan,
        });
        p2.ShouldStop(StateAt(1, 0.95m)).ShouldBeTrue();
        p2.Parameters["threshold"].ShouldBe(0.9m);
        p2.Parameters["direction"].ShouldBe(ThresholdDirection.GreaterThan);
    }

    [Fact]
    public void FieldThreshold_UnknownField_ThrowsAtShouldStop()
    {
        var p = new FieldThreshold<ScoreSig>("DoesNotExist", 0.5m, ThresholdDirection.LessThan);
        Should.Throw<InvalidOperationException>(() => p.ShouldStop(StateAt(1, 0.4m)));
    }

    [Fact]
    public void FieldThreshold_NullFieldName_ThrowsAtCtor() =>
        Should.Throw<ArgumentNullException>(() => new FieldThreshold<ScoreSig>(null!, 0.5m, ThresholdDirection.LessThan));

    // ---------- FieldChangeBelow ----------

    [Fact]
    public void FieldChangeBelow_Iteration1_ReturnsFalse()
    {
        var p = new FieldChangeBelow<ScoreSig>("Score", 0.01m);
        // No previous prediction in history â€” convergence cannot be determined on iter 1.
        p.ShouldStop(StateAt(1, 0.5m)).ShouldBeFalse();
    }

    [Fact]
    public void FieldChangeBelow_StopsWhenChangeBelowEpsilon()
    {
        var prev = new Prediction<ScoreSig>(new ScoreSig { Score = 0.500m }, null!);
        var prev2 = new Prediction<ScoreSig>(new ScoreSig { Score = 0.501m }, null!);
        var p = new FieldChangeBelow<ScoreSig>("Score", 0.01m);
        p.ShouldStop(StateAt(2, 0.502m, history: new[] { prev, prev2 })).ShouldBeTrue();
    }

    [Fact]
    public void FieldChangeBelow_DoesNotStop_WhenChangeAboveEpsilon()
    {
        var prev = new Prediction<ScoreSig>(new ScoreSig { Score = 0.500m }, null!);
        var prev2 = new Prediction<ScoreSig>(new ScoreSig { Score = 0.501m }, null!);
        var p = new FieldChangeBelow<ScoreSig>("Score", 0.01m);
        p.ShouldStop(StateAt(2, 0.700m, history: new[] { prev, prev2 })).ShouldBeFalse();
    }

    [Fact]
    public void FieldChangeBelow_Parameters_ExposeFieldAndEpsilon()
    {
        var p = new FieldChangeBelow<ScoreSig>("Score", 0.005m);
        p.Parameters["field"].ShouldBe("Score");
        p.Parameters["epsilon"].ShouldBe(0.005m);
    }

    [Fact]
    public void FieldChangeBelow_WithParameters_Rebinds()
    {
        var p = new FieldChangeBelow<ScoreSig>("Score", 0.01m);
        var p2 = p.WithParameters(new Dictionary<string, object?> { ["field"] = "Score", ["epsilon"] = 0.5m });
        p2.Parameters["epsilon"].ShouldBe(0.5m);
        p2.ShouldNotBeSameAs(p);
    }

    // ---------- CompositeAll / CompositeAny ----------

    [Fact]
    public void CompositeAll_AllTrue_ReturnsTrue()
    {
        var p = new CompositeAll<ScoreSig>(
            new FieldThreshold<ScoreSig>("Score", 0.5m, ThresholdDirection.LessThan),
            new MaxIterations<ScoreSig>(2));
        p.ShouldStop(StateAt(2, 0.4m)).ShouldBeTrue();
    }

    [Fact]
    public void CompositeAll_OneFalse_ReturnsFalse()
    {
        var p = new CompositeAll<ScoreSig>(
            new FieldThreshold<ScoreSig>("Score", 0.5m, ThresholdDirection.LessThan),
            new MaxIterations<ScoreSig>(5));
        p.ShouldStop(StateAt(2, 0.4m)).ShouldBeFalse();
    }

    [Fact]
    public void CompositeAll_EmptyArray_ReturnsTrue()
    {
        // Vacuous truth: AND over zero predicates is `true`.
        var p = new CompositeAll<ScoreSig>();
        p.ShouldStop(StateAt(1, 0)).ShouldBeTrue();
    }

    [Fact]
    public void CompositeAll_Parameters_ExposesCount()
    {
        var p = new CompositeAll<ScoreSig>(new MaxIterations<ScoreSig>(3), new MaxIterations<ScoreSig>(5));
        p.Parameters["count"].ShouldBe(2);
    }

    [Fact]
    public void CompositeAll_WithParameters_ReturnsNewInstance()
    {
        var inner = new MaxIterations<ScoreSig>(3);
        var p = new CompositeAll<ScoreSig>(inner);
        var p2 = p.WithParameters(new Dictionary<string, object?>());
        p2.ShouldNotBeSameAs(p);
    }

    [Fact]
    public void CompositeAny_FirstTrue_ShortCircuits()
    {
        var calls = 0;
        var counting = new CountingPredicate(_ => { calls++; return false; });
        var p = new CompositeAny<ScoreSig>(
            new MaxIterations<ScoreSig>(1),  // true on iter >= 1
            counting);
        p.ShouldStop(StateAt(1, 0)).ShouldBeTrue();
        calls.ShouldBe(0);
    }

    [Fact]
    public void CompositeAny_AllFalse_ReturnsFalse()
    {
        var p = new CompositeAny<ScoreSig>(
            new FieldThreshold<ScoreSig>("Score", 0.5m, ThresholdDirection.GreaterThan),
            new MaxIterations<ScoreSig>(99));
        p.ShouldStop(StateAt(1, 0.4m)).ShouldBeFalse();
    }

    [Fact]
    public void CompositeAny_EmptyArray_ReturnsFalse()
    {
        // Vacuous false: OR over zero predicates is `false`.
        var p = new CompositeAny<ScoreSig>();
        p.ShouldStop(StateAt(1, 0)).ShouldBeFalse();
    }

    [Fact]
    public void CompositeAny_Parameters_ExposesCount()
    {
        var p = new CompositeAny<ScoreSig>(new MaxIterations<ScoreSig>(3), new MaxIterations<ScoreSig>(5));
        p.Parameters["count"].ShouldBe(2);
    }

    [Fact]
    public void CompositeAny_WithParameters_ReturnsNewInstance()
    {
        var inner = new MaxIterations<ScoreSig>(3);
        var p = new CompositeAny<ScoreSig>(inner);
        var p2 = p.WithParameters(new Dictionary<string, object?>());
        p2.ShouldNotBeSameAs(p);
    }

    sealed class CountingPredicate(Func<LoopState<ScoreSig>, bool> impl) : LoopPredicate<ScoreSig>
    {
        public override bool ShouldStop(LoopState<ScoreSig> state) => impl(state);
        public override IReadOnlyDictionary<string, object?> Parameters { get; } = new Dictionary<string, object?>();
        public override LoopPredicate<ScoreSig> WithParameters(IReadOnlyDictionary<string, object?> parameters) => this;
    }

    // ---------- LoopPredicateRegistry ----------

    [Fact]
    public void Registry_RegisterCreate_RoundTripsForCustomPredicate()
    {
        // Register a custom factory under a unique name to avoid colliding with sibling tests.
        var name = $"my_max_{Guid.NewGuid():N}";
        LoopPredicateRegistry.Register<ScoreSig>(name,
            p => new MaxIterations<ScoreSig>((int)p["max"]!));
        var built = LoopPredicateRegistry.Create<ScoreSig>(name,
            new Dictionary<string, object?> { ["max"] = 4 });
        built.ShouldBeOfType<MaxIterations<ScoreSig>>();
        built.Parameters["max"].ShouldBe(4);
    }

    [Fact]
    public void Registry_Names_ListsRegistered()
    {
        var name = $"named_{Guid.NewGuid():N}";
        LoopPredicateRegistry.Register<ScoreSig>(name,
            _ => new MaxIterations<ScoreSig>(1));
        LoopPredicateRegistry.Names<ScoreSig>().ShouldContain(name);
    }

    [Fact]
    public void Registry_Create_UnknownName_Throws()
    {
        Should.Throw<KeyNotFoundException>(() =>
            LoopPredicateRegistry.Create<ScoreSig>("does-not-exist",
                new Dictionary<string, object?>()));
    }

    [Fact]
    public void Registry_Register_NullName_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            LoopPredicateRegistry.Register<ScoreSig>(null!,
                _ => new MaxIterations<ScoreSig>(1)));
    }

    [Fact]
    public void Registry_Register_NullFactory_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            LoopPredicateRegistry.Register<ScoreSig>("name", null!));
    }

    [Fact]
    public void Registry_PerTypeIsolation_TestSig_DoesNotSeeScoreSigEntries()
    {
        var name = $"score_only_{Guid.NewGuid():N}";
        LoopPredicateRegistry.Register<ScoreSig>(name, _ => new MaxIterations<ScoreSig>(1));
        LoopPredicateRegistry.Names<TestSig>().ShouldNotContain(name);
    }
}

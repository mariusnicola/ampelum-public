using Shouldly;
using SignalGrid.Ampelum.Optimizers;
using Xunit;

namespace Ampelum.Tests.Optimizers;

public sealed class TpeSamplerTests
{
    static List<(int[] Choice, double Score)> Trials(params (int[] Choice, double Score)[] trials) => [.. trials];

    [Fact]
    public void Suggest_PrefersTheCategoryThatScoredWell()
    {
        var sampler = new TpeSampler([3], randomTrials: 2, seed: 1);
        var finished = Trials(
            ([0], 0.10), ([1], 0.20), ([2], 0.90), ([2], 0.95),
            ([0], 0.00), ([1], 0.10), ([2], 1.00), ([0], 0.05));

        for (int i = 0; i < 100; i++)
            sampler.Suggest(finished).ShouldBe([2]);
    }

    [Fact]
    public void Suggest_PrefersTheBestCategoryOfEachParameter()
    {
        var sampler = new TpeSampler([3, 2], randomTrials: 2, seed: 5);
        var finished = Trials(
            ([0, 0], 0.0), ([1, 0], 0.1), ([2, 1], 1.0), ([2, 1], 0.9),
            ([0, 1], 0.2), ([1, 0], 0.0), ([2, 1], 0.95), ([0, 0], 0.1));

        for (int i = 0; i < 100; i++)
            sampler.Suggest(finished).ShouldBe([2, 1]);
    }

    [Fact]
    public void Suggest_GoodGroupIsTheTopQuarterAndAtLeastOneTrial()
    {
        // Two trials: the quarter rounds down to zero, so one trial is the good group.
        var sampler = new TpeSampler([4], randomTrials: 1, seed: 3);
        var finished = Trials(([3], 0.9), ([0], 0.1));

        for (int i = 0; i < 100; i++)
            sampler.Suggest(finished).ShouldBe([3]);
    }

    [Fact]
    public void Suggest_GoodGroupOfEightTrialsIsTheTopTwo_NotTheTopHalf()
    {
        // With the top two as the good group, category 0 has the highest l/g (2.7 against 0.6 and 0.45).
        // With the top four it would be category 1 (3.0 against 1.5 and 0.25), so a wider group picks 1.
        var sampler = new TpeSampler([3], randomTrials: 1, seed: 7);
        var finished = Trials(
            ([0], 1.00), ([0], 0.99), ([1], 0.80), ([1], 0.79),
            ([2], 0.30), ([2], 0.20), ([2], 0.10), ([0], 0.00));

        for (int i = 0; i < 100; i++)
            sampler.Suggest(finished).ShouldBe([0]);
    }

    [Fact]
    public void Suggest_BelowTheRandomTrialCountIsUniformRandom()
    {
        var sampler = new TpeSampler([2], randomTrials: 5, seed: 11);
        // Four finished trials all say category 1 is best, but four is still the random phase.
        var finished = Trials(([1], 1.0), ([0], 0.0), ([0], 0.0), ([0], 0.0));

        var picks = Enumerable.Range(0, 200).Select(_ => sampler.Suggest(finished)[0]).ToList();

        picks.ShouldContain(0);
        picks.ShouldContain(1);
    }

    [Fact]
    public void Suggest_AtTheRandomTrialCountSwitchesToTheEstimator()
    {
        var sampler = new TpeSampler([2], randomTrials: 5, seed: 11);
        var finished = Trials(([1], 1.0), ([0], 0.0), ([0], 0.0), ([0], 0.0), ([0], 0.0));

        for (int i = 0; i < 100; i++)
            sampler.Suggest(finished).ShouldBe([1]);
    }

    [Fact]
    public void Suggest_SameSeedGivesTheSameSequence_DifferentSeedDoesNot()
    {
        var finished = Trials(([0], 0.3), ([5], 0.9), ([2], 0.1), ([7], 0.6));

        List<int> Sequence(int seed, int randomTrials)
        {
            var s = new TpeSampler([1000], randomTrials, seed);
            return [.. Enumerable.Range(0, 30).Select(_ => s.Suggest(finished)[0])];
        }

        // randomTrials 9 keeps these four finished trials in the random phase; 2 puts them in the estimator.
        Sequence(4, 9).ShouldBe(Sequence(4, 9));
        Sequence(4, 9).ShouldNotBe(Sequence(5, 9));
        Sequence(4, 2).ShouldBe(Sequence(4, 2));
        Sequence(4, 2).ShouldNotBe(Sequence(5, 2));
    }

    [Fact]
    public void Suggest_SingleCategoryAlwaysReturnsIt()
    {
        var sampler = new TpeSampler([1], randomTrials: 1, seed: 2);
        sampler.Suggest([]).ShouldBe([0]);
        sampler.Suggest(Trials(([0], 0.5))).ShouldBe([0]);
    }

    [Fact]
    public void Suggest_NoParametersReturnsAnEmptyChoice()
    {
        var sampler = new TpeSampler([], randomTrials: 1, seed: 2);
        sampler.Suggest([]).ShouldBeEmpty();
        sampler.Suggest(Trials(([], 0.5))).ShouldBeEmpty();
    }

    [Fact]
    public void Suggest_EveryChoiceStaysInRange()
    {
        var sampler = new TpeSampler([3, 5], randomTrials: 2, seed: 8);
        var finished = new List<(int[] Choice, double Score)>();
        for (int i = 0; i < 60; i++)
        {
            var choice = sampler.Suggest(finished);
            choice[0].ShouldBeInRange(0, 2);
            choice[1].ShouldBeInRange(0, 4);
            finished.Add((choice, choice[0] == 1 ? 1.0 : 0.0));
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 1)]
    [InlineData(10, 2)]
    [InlineData(50, 10)]
    public void RandomTrials_IsAFifthOfTheTrialsAndAtLeastOne(int numTrials, int expected) =>
        MiproV2<object>.RandomTrials(numTrials).ShouldBe(expected);
}

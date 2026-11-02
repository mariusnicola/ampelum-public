using SignalGrid.Ampelum.Optimizers;
using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Optimizers;

/// <summary>
/// Closes coverage on the OperationCanceledException re-throw arm and the general
/// catch-and-skip arm in BootstrapFewShotWithRandomSearch.ScoreCandidateAsync and
/// Copro.CompileAsync's per-example evaluation block.
/// </summary>
public sealed class OptimizerScoringExceptionCoverageTests
{
    sealed class FakeProg : IComposedModule
    {
        public IReadOnlyDictionary<string, object> Submodules =>
            new Dictionary<string, object>();
        public object Rebind(IReadOnlyDictionary<string, object> r) => this;
    }

    sealed class WithContextProg
    {
#pragma warning disable CA1822
        public WithContextProg WithContext(AmpelumContext ctx) => this;
#pragma warning restore CA1822
    }

    sealed class TrueMetric : IMetric
    {
        public double Score(object actual, object expected, SignatureMetadata signature) => 1.0;
    }

    static List<Example> MakeTrainset(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new Example(
                new Dictionary<string, object?> { ["q"] = $"q{i}" },
                new Dictionary<string, object?> { ["a"] = $"a{i}" }))
            .ToList();

    static Task NoopRunner(FakeProg p, Example e, AmpelumContext ctx, CancellationToken ct) =>
        Task.CompletedTask;

    [Fact]
    public async Task BootstrapRandom_ScoringRunner_ThrowsGeneric_IsSwallowed()
    {
        var optimizer = new BootstrapFewShotWithRandomSearch<FakeProg>(
            NoopRunner,
            numCandidates: 1,
            maxBootstrappedDemos: 0,
            seed: 0,
            scoringRunner: (_, _, _, _) => throw new InvalidOperationException("boom"));

        var result = await optimizer.CompileAsync(
            new FakeProg(), MakeTrainset(2), new TrueMetric(),
            TestHelpers.MakeContext(), null, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        // All examples threw â†’ evaluated == 0 â†’ score 0.
        result.Score.ShouldBe(0.0);
    }

    [Fact]
    public async Task BootstrapRandom_ScoringRunner_ThrowsOCE_Propagates()
    {
        var optimizer = new BootstrapFewShotWithRandomSearch<FakeProg>(
            NoopRunner,
            numCandidates: 1,
            maxBootstrappedDemos: 0,
            seed: 0,
            scoringRunner: (_, _, _, _) =>
                throw new OperationCanceledException("scoring cancelled"));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            optimizer.CompileAsync(
                new FakeProg(), MakeTrainset(1), new TrueMetric(),
                TestHelpers.MakeContext(), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Copro_ProgramRunner_ThrowsOCE_Propagates()
    {
        var proposer = new SignalGrid.Ampelum.Testing.FakeChatClient();
        for (int i = 0; i < 8; i++) proposer.EnqueueText("Better.");
        var optimizer = new Copro<WithContextProg>(
            instructionProposer: proposer,
            candidatesPerSubmodule: 1,
            programRunner: (_, _, _, _) =>
                throw new OperationCanceledException("runner cancelled"));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            optimizer.CompileAsync(
                new WithContextProg(), MakeTrainset(1), new TrueMetric(),
                TestHelpers.MakeContext(), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Copro_ProgramRunner_ThrowsGeneric_IsSwallowed()
    {
        // Per-example catch-and-skip block in Copro.CompileAsync. Generic exception is swallowed,
        // OCE re-throw arm is exercised by the same try/catch shape under cancellation tests.
        var proposer = new SignalGrid.Ampelum.Testing.FakeChatClient();
        for (int i = 0; i < 8; i++) proposer.EnqueueText("Better.");
        var optimizer = new Copro<WithContextProg>(
            instructionProposer: proposer,
            candidatesPerSubmodule: 1,
            programRunner: (_, _, _, _) =>
                throw new InvalidOperationException("runner failed"));

        var result = await optimizer.CompileAsync(
            new WithContextProg(), MakeTrainset(1), new TrueMetric(),
            TestHelpers.MakeContext(), null, TestContext.Current.CancellationToken);
        result.ShouldNotBeNull();
    }
}

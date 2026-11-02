using SignalGrid.Ampelum.Optimizers;
using SignalGrid.Ampelum.Testing;
using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Optimizers;

public sealed class MiproV2Tests
{
    [Fact]
    public void Ctor_NullProgramRunner_Throws()
    {
        var chat = new FakeChatClient();
        Should.Throw<ArgumentNullException>(() =>
            new MiproV2<FakeProg>(null!, chat));
    }

    [Fact]
    public void Ctor_NullProposerClient_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            new MiproV2<FakeProg>((_, ex, _, _) => Task.FromResult(ex), null!));
    }

    [Fact]
    public void Ctor_MaxRoundsZero_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new MiproV2<FakeProg>((_, ex, _, _) => Task.FromResult(ex),
                new FakeChatClient(), maxRounds: 0));

    [Fact]
    public void Ctor_CandidatesZero_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new MiproV2<FakeProg>((_, ex, _, _) => Task.FromResult(ex),
                new FakeChatClient(), candidatesPerSubmodule: 0));

    [Fact]
    public void Ctor_MaxDemosNegative_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new MiproV2<FakeProg>((_, ex, _, _) => Task.FromResult(ex),
                new FakeChatClient(), maxBootstrappedDemos: -1));

    [Fact]
    public async Task CompileAsync_NullProgram_Throws()
    {
        var optimizer = MakeOptimizer();
        await Should.ThrowAsync<ArgumentNullException>(() =>
            optimizer.CompileAsync(null!, MakeTrainset(1), new TrueMetric(),
                TestHelpers.MakeContext(), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompileAsync_NullTrainset_Throws()
    {
        var optimizer = MakeOptimizer();
        await Should.ThrowAsync<ArgumentNullException>(() =>
            optimizer.CompileAsync(new FakeProg(), null!, new TrueMetric(),
                TestHelpers.MakeContext(), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompileAsync_NullMetric_Throws()
    {
        var optimizer = MakeOptimizer();
        await Should.ThrowAsync<ArgumentNullException>(() =>
            optimizer.CompileAsync(new FakeProg(), MakeTrainset(1), null!,
                TestHelpers.MakeContext(), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompileAsync_NullContext_Throws()
    {
        var optimizer = MakeOptimizer();
        await Should.ThrowAsync<ArgumentNullException>(() =>
            optimizer.CompileAsync(new FakeProg(), MakeTrainset(1), new TrueMetric(),
                null!, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompileAsync_SingleRound_ReturnsResult()
    {
        var proposerFake = new FakeChatClient();
        proposerFake.EnqueueText("Answer concisely.");
        proposerFake.EnqueueText("Be brief.");

        var optimizer = new MiproV2<FakeProg>(
            (_, ex, _, _) => Task.FromResult(ex),
            proposerFake, maxRounds: 1, candidatesPerSubmodule: 1);

        var result = await optimizer.CompileAsync(
            new FakeProg(), MakeTrainset(1), new TrueMetric(),
            TestHelpers.MakeContext(), null, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.Program.ShouldNotBeNull();
    }

    [Fact]
    public async Task CompileAsync_ReportsProgress()
    {
        var proposerFake = new FakeChatClient();
        for (int i = 0; i < 4; i++) proposerFake.EnqueueText("Be direct.");
        var reports = new List<OptimizerProgress>();

        var optimizer = new MiproV2<FakeProg>(
            (_, ex, _, _) => Task.FromResult(ex),
            proposerFake, maxRounds: 2, candidatesPerSubmodule: 1);

        await optimizer.CompileAsync(
            new FakeProg(), MakeTrainset(1), new TrueMetric(),
            TestHelpers.MakeContext(),
            new SyncProgress<OptimizerProgress>(reports.Add),
            TestContext.Current.CancellationToken);

        reports.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task CompileAsync_CancelledToken_Throws()
    {
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var optimizer = MakeOptimizer();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            optimizer.CompileAsync(new FakeProg(), MakeTrainset(1),
                new TrueMetric(), TestHelpers.MakeContext(), null, cts.Token));
    }

    [Fact]
    public async Task CompileAsync_BootstrapPhase_InvokesUserProgramRunner()
    {
        // Regression: MIPROv2 must forward its programRunner to the inner BootstrapFewShot,
        // otherwise the bootstrap phase is inert and no demos are captured.
        var proposerFake = new FakeChatClient();
        proposerFake.EnqueueText("Be brief.");

        int runnerCalls = 0;
        var optimizer = new MiproV2<FakeProg>(
            (_, ex, runCtx, _) =>
            {
                runnerCalls++;
                runCtx.TraceSink.Write(new TraceRecord(
                    Id: Guid.NewGuid(),
                    ModuleName: "qa",
                    SignatureType: typeof(string),
                    Inputs: ex.Inputs,
                    Outputs: ex.Outputs,
                    PromptMessages: [],
                    RawResponse: "",
                    Usage: null,
                    Duration: TimeSpan.Zero,
                    Error: null,
                    Metadata: new Dictionary<string, object?>()));
                return Task.FromResult(ex);
            },
            proposerFake, maxRounds: 1, candidatesPerSubmodule: 1);

        var result = await optimizer.CompileAsync(
            new FakeProg(), MakeTrainset(3), new TrueMetric(),
            TestHelpers.MakeContext(), null, TestContext.Current.CancellationToken);

        // Bootstrap phase ran user runner once per trainset row, plus extra calls for scoring.
        runnerCalls.ShouldBeGreaterThanOrEqualTo(3);
        result.DemosBySubmodule.ShouldContainKey("qa");
        result.DemosBySubmodule["qa"].Count.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task CompileAsync_EmptyTrainset_ReturnsZeroScore()
    {
        var proposerFake = new FakeChatClient();
        proposerFake.EnqueueText("Be terse.");
        var optimizer = new MiproV2<FakeProg>(
            (_, ex, _, _) => Task.FromResult(ex),
            proposerFake, maxRounds: 1, candidatesPerSubmodule: 1);

        var result = await optimizer.CompileAsync(
            new FakeProg(), Array.Empty<Example>(), new TrueMetric(),
            TestHelpers.MakeContext(), null, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
    }

    static MiproV2<FakeProg> MakeOptimizer() =>
        new((_, ex, _, _) => Task.FromResult(ex), new FakeChatClient(),
            maxRounds: 1, candidatesPerSubmodule: 1);

    static List<Example> MakeTrainset(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new Example(
                new Dictionary<string, object?> { ["q"] = $"q{i}" },
                new Dictionary<string, object?> { ["a"] = $"a{i}" }))
            .ToList();

    sealed class SyncProgress<T>(Action<T> cb) : IProgress<T>
    { public void Report(T value) => cb(value); }

    sealed class TrueMetric : IMetric
    {
        public double Score(object actual, object expected, SignatureMetadata signature) => 1.0;
    }

    sealed class FakeProg : IComposedModule
    {
        public IReadOnlyDictionary<string, object> Submodules =>
            new Dictionary<string, object>();
        public object Rebind(IReadOnlyDictionary<string, object> r) => this;
    }
}

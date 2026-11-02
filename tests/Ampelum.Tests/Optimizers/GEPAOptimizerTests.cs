using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Optimizers;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Optimizers;

[Collection("Orchestration")]
public sealed class GEPAOptimizerTests
{
    [Signature("Given a question, produce an answer.")]
    public sealed record QASig : ISignature
    {
        [Input] public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    static Dictionary<string, object?> In(string k, string v) =>
        new() { [k] = v };

    [Fact]
    public async Task CompileAsync_EmptyTrainset_Throws()
    {
        var sut = new GEPAOptimizer<Predict<QASig>>(
            new FakeChatClient(), new FakeChatClient());

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.CompileAsync(
                PredictFor<QASig>(),
                [],
                new SingleScoreMetric(1.0),
                DummyContext,
                ct: TestContext.Current.CancellationToken));

        ex.ParamName.ShouldBe("trainset");
    }

    [Fact]
    public async Task CompileAsync_SingleExample_ProducesResult()
    {
        var fake = new FakeChatClient();
        // Total LLM calls: 1 baseline score + 2 candidates = 3 calls
        fake.EnqueueText("baseline");
        fake.EnqueueText("better 1");
        fake.EnqueueText("better 2");

        var sut = new GEPAOptimizer<Predict<QASig>>(fake, fake,
            new GepaOptions { CandidateCount = 2, MaxIterations = 1 });

        var trainset = new[]
        {
            new Example(
                new Dictionary<string, object?> { ["Question"] = "What is 2+2?" },
                new Dictionary<string, object?> { ["Answer"] = "4" })
        };

        var result = await sut.CompileAsync(
            PredictFor<QASig>(),
            trainset,
            new SingleScoreMetric(1.0),
            DummyContext,
            ct: TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.Score.ShouldNotBeNull();
        result.DemosBySubmodule.ShouldNotBeNull();
        result.TrainsetSize.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task CompileAsync_ReportsProgress()
    {
        var fake = new FakeChatClient();
        // 1 baseline + (2 candidates × 2 iterations) = 5 LLM calls
        fake.EnqueueText("baseline");
        fake.EnqueueText("a"); fake.EnqueueText("b");
        fake.EnqueueText("c"); fake.EnqueueText("d");

        var progressEvents = new List<OptimizerProgress>();
        IProgress<OptimizerProgress> progress = new Progress<OptimizerProgress>(p => progressEvents.Add(p));

        var sut = new GEPAOptimizer<Predict<QASig>>(fake, fake,
            new GepaOptions { BootstrapCount = 2, CandidateCount = 2, MaxIterations = 2 });

        var trainset = new[]
        {
            new Example(In("Q", "q1"), In("A", "a1")),
            new Example(In("Q", "q2"), In("A", "a2")),
            new Example(In("Q", "q3"), In("A", "a3")),
        };

        var result = await sut.CompileAsync(
            PredictFor<QASig>(),
            trainset,
            new SingleScoreMetric(1.0),
            DummyContext,
            progress,
            TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        progressEvents.ShouldNotBeEmpty();
        progressEvents.ShouldContain(p => p.Phase == "bootstrap");
    }

    [Fact]
    public async Task CompileAsync_NoCandidates_ReturnsBaseline()
    {
        var sut = new GEPAOptimizer<Predict<QASig>>(
            new FakeChatClient(), new FakeChatClient(),
            new GepaOptions { CandidateCount = 0, MaxIterations = 0 });

        var trainset = new[]
        {
            new Example(In("X", "x"), In("Y", "y"))
        };

        var result = await sut.CompileAsync(
            PredictFor<QASig>(),
            trainset,
            new SingleScoreMetric(1.0),
            DummyContext,
            ct: TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        // SingleScoreMetric(1.0) always returns 1.0, so baseline = 1.0
        result.Score.ShouldBe(1.0);
    }

    [Fact]
    public async Task CompileAsync_WithGoodCandidate_Accepts()
    {
        var fake = new FakeChatClient();
        // 1 baseline + 2 candidates × 1 iteration = 3 LLM calls
        fake.EnqueueText("baseline");
        fake.EnqueueText("improved instruction");
        fake.EnqueueText("variant two");

        var sut = new GEPAOptimizer<Predict<QASig>>(fake, fake,
            new GepaOptions { BootstrapCount = 2, CandidateCount = 2, MaxIterations = 1, ImprovementThreshold = 0.01 });

        var trainset = new[]
        {
            new Example(In("Question", "a"), In("Answer", "A")),
            new Example(In("Question", "b"), In("Answer", "B")),
            new Example(In("Question", "c"), In("Answer", "C")),
        };

        var result = await sut.CompileAsync(
            PredictFor<QASig>(),
            trainset,
            new SingleScoreMetric(1.0),
            DummyContext,
            ct: TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.TrainsetSize.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void GepaOptions_Defaults()
    {
        var d = new GepaOptions();
        d.BootstrapCount.ShouldBe(4);
        d.CandidateCount.ShouldBe(3);
        d.MaxIterations.ShouldBe(3);
        d.ImprovementThreshold.ShouldBe(0.05);
        d.ValidationHoldoutPercent.ShouldBe(20);
        d.QualityThreshold.ShouldBe(0.5);
    }

    [Fact]
    public void Constructor_NullOptimizerLlm_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new GEPAOptimizer<Predict<QASig>>(null!, new FakeChatClient()));
    }

    [Fact]
    public void Constructor_NullProgramLlm_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new GEPAOptimizer<Predict<QASig>>(new FakeChatClient(), null!));
    }

    [Fact]
    public void Constructor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new GEPAOptimizer<Predict<QASig>>(
                new FakeChatClient(), new FakeChatClient(), null!));
    }

    [Fact]
    public async Task CompileAsync_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var fake = new FakeChatClient();
        fake.EnqueueText("x");

        var sut = new GEPAOptimizer<Predict<QASig>>(fake, fake);
        var trainset = new[]
        {
            new Example(In("X", "x"), In("Y", "y"))
        };

        // Accept TaskCanceledException (derives from OperationCanceledException)
        var ex = await Record.ExceptionAsync(() =>
            sut.CompileAsync(
                PredictFor<QASig>(),
                trainset,
                new SingleScoreMetric(1.0),
                DummyContext,
                ct: cts.Token));
        ex.ShouldBeAssignableTo<OperationCanceledException>();
    }

    [Fact]
    public async Task CompileAsync_RunsHookPipeline_AroundCandidateGeneration()
    {
        var fake = new FakeChatClient();
        fake.EnqueueText("baseline");
        fake.EnqueueText("candidate");

        var hook = new SpyHook();
        var ctx = DummyContext with { Hooks = [hook] };

        var sut = new GEPAOptimizer<Predict<QASig>>(fake, fake,
            new GepaOptions { CandidateCount = 1, MaxIterations = 1 });

        var trainset = new[]
        {
            new Example(
                new Dictionary<string, object?> { ["Question"] = "What is 2+2?" },
                new Dictionary<string, object?> { ["Answer"] = "4" })
        };

        await sut.CompileAsync(
            PredictFor<QASig>(),
            trainset,
            new SingleScoreMetric(1.0),
            ctx,
            ct: TestContext.Current.CancellationToken);

        hook.Calls.ShouldContain("before:GEPAOptimizer.GenerateCandidate");
        hook.Calls.ShouldContain("after:GEPAOptimizer.GenerateCandidate");
    }

    sealed class SpyHook : SignalGrid.Ampelum.Hooks.IHook
    {
        public int Order => 0;
        public List<string> Calls { get; } = new();

        public ValueTask OnBeforeAsync(SignalGrid.Ampelum.Hooks.HookContext ctx, CancellationToken ct)
        {
            Calls.Add($"before:{ctx.ModuleName}");
            return default;
        }

        public ValueTask OnAfterAsync(SignalGrid.Ampelum.Hooks.HookContext ctx, CancellationToken ct)
        {
            Calls.Add($"after:{ctx.ModuleName}");
            return default;
        }

        public ValueTask OnErrorAsync(SignalGrid.Ampelum.Hooks.HookContext ctx, Exception error, CancellationToken ct)
        {
            Calls.Add($"error:{ctx.ModuleName}");
            return default;
        }
    }

    // --- helpers ---

    static AmpelumContext DummyContext => new(
        new FakeChatClient(),
        new ChatAdapter(),
        NullTraceSink.Instance,
        NullLoggerFactory.Instance);

    static Predict<TSig> PredictFor<TSig>() where TSig : ISignature =>
        new(DummyContext);

    /// <summary>Always returns the configured value.</summary>
    sealed class SingleScoreMetric(double score) : IMetric
    {
        public double Score(object actual, object expected, SignatureMetadata signature) => score;
    }
}

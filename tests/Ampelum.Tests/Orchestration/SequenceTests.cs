namespace Ampelum.Tests.Orchestration;

using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Composition;
using SignalGrid.Ampelum.Orchestration;
using Ampelum.Tests.Composition._Sigs;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

public sealed class SequenceTests
{
    static TraceRecord Dummy(string modName) => new(
        Id: Guid.NewGuid(),
        ModuleName: modName,
        SignatureType: typeof(object),
        Inputs: new Dictionary<string, object?>(),
        Outputs: new Dictionary<string, object?>(),
        PromptMessages: [],
        RawResponse: "",
        Usage: null,
        Duration: TimeSpan.Zero,
        Error: null,
        Metadata: new Dictionary<string, object?>());

    sealed class QuestionToAnswer : IModule<TestSig, ScoreSig>
    {
        public Task<Prediction<ScoreSig>> ForwardAsync(TestSig inputs, CancellationToken ct) =>
            Task.FromResult(new Prediction<ScoreSig>(
                new ScoreSig { Question = inputs.Question, Score = inputs.Question.Length },
                Dummy("q2s")));
    }

    sealed class ScoreToAnswer : IModule<ScoreSig, TestSig>
    {
        public Task<Prediction<TestSig>> ForwardAsync(ScoreSig inputs, CancellationToken ct) =>
            Task.FromResult(new Prediction<TestSig>(
                new TestSig { Question = inputs.Question, Answer = $"score:{inputs.Score}" },
                Dummy("s2a")));
    }

    sealed class AppendToAnswer : IModule<TestSig, TestSig>
    {
        readonly string _suffix;
        public AppendToAnswer(string suffix) => _suffix = suffix;

        public Task<Prediction<TestSig>> ForwardAsync(TestSig inputs, CancellationToken ct) =>
            Task.FromResult(new Prediction<TestSig>(
                inputs with { Answer = inputs.Answer + _suffix },
                Dummy("append")));
    }

    sealed class ScoreMultiply : IModule<ScoreSig, ScoreSig>
    {
        readonly int _factor;
        public ScoreMultiply(int factor) => _factor = factor;

        public Task<Prediction<ScoreSig>> ForwardAsync(ScoreSig inputs, CancellationToken ct) =>
            Task.FromResult(new Prediction<ScoreSig>(
                inputs with { Score = inputs.Score * _factor },
                Dummy("mul")));
    }

    sealed class Identity<T> : IModule<T, T> where T : ISignature
    {
        public Task<Prediction<T>> ForwardAsync(T inputs, CancellationToken ct) =>
            Task.FromResult(new Prediction<T>(inputs, Dummy("id")));
    }

    sealed class ThrowingStage<TIn, TOut> : IModule<TIn, TOut>
        where TIn : ISignature
        where TOut : ISignature
    {
        public Task<Prediction<TOut>> ForwardAsync(TIn inputs, CancellationToken ct) =>
            throw new InvalidOperationException("stage_fail");
    }

    [Fact]
    public async Task Sequence2_SingleStage()
    {
        var seq = new Sequence<TestSig, TestSig>(new Identity<TestSig>());

        var input = new TestSig { Question = "hello", Answer = "world" };
        var result = await seq.ForwardAsync(input, TestContext.Current.CancellationToken);

        result.Value.Question.ShouldBe("hello");
        result.Value.Answer.ShouldBe("world");
    }

    [Fact]
    public async Task Sequence3_TwoStages_Pipeline()
    {
        var seq = new Sequence<TestSig, ScoreSig, TestSig>(
            new QuestionToAnswer(), new ScoreToAnswer());

        var result = await seq.ForwardAsync(new TestSig { Question = "hello" }, TestContext.Current.CancellationToken);

        result.Value.Answer.ShouldBe("score:5");
    }

    [Fact]
    public async Task Sequence4_ThreeStages()
    {
        var seq = new Sequence<TestSig, ScoreSig, ScoreSig, TestSig>(
            new QuestionToAnswer(),
            new ScoreMultiply(2),
            new ScoreToAnswer());

        var result = await seq.ForwardAsync(new TestSig { Question = "hi" }, TestContext.Current.CancellationToken);

        result.Value.Answer.ShouldBe("score:4"); // "hi".Length=2, *2 = 4
    }

    [Fact]
    public async Task Sequence5_FourStages()
    {
        var seq = new Sequence<TestSig, ScoreSig, ScoreSig, TestSig, TestSig>(
            new QuestionToAnswer(),
            new ScoreMultiply(3),
            new ScoreToAnswer(),
            new AppendToAnswer("!"));

        var result = await seq.ForwardAsync(new TestSig { Question = "ab" }, TestContext.Current.CancellationToken);

        result.Value.Answer.ShouldBe("score:6!"); // "ab".Length=2, *3 = 6
    }

    [Fact]
    public async Task ErrorInStage1_Propagates()
    {
        var seq = new Sequence<TestSig, ScoreSig, TestSig>(
            new ThrowingStage<TestSig, ScoreSig>(),
            new ScoreToAnswer());

        await Should.ThrowAsync<InvalidOperationException>(() =>
            seq.ForwardAsync(new TestSig { Question = "x" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ErrorInMiddleStage_Propagates()
    {
        var seq = new Sequence<TestSig, ScoreSig, ScoreSig, TestSig>(
            new QuestionToAnswer(),
            new ThrowingStage<ScoreSig, ScoreSig>(),
            new ScoreToAnswer());

        await Should.ThrowAsync<InvalidOperationException>(() =>
            seq.ForwardAsync(new TestSig { Question = "x" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StateThreading_EachStageGetsPreviousOutput()
    {
        var seq = new Sequence<TestSig, ScoreSig, TestSig>(
            new QuestionToAnswer(),
            new ScoreToAnswer());

        var result = await seq.ForwardAsync(new TestSig { Question = "hello" }, TestContext.Current.CancellationToken);

        result.Value.Question.ShouldBe("hello"); // preserved
        result.Value.Answer.ShouldBe("score:5"); // derived from ScoreSig.Score
    }

    [Fact]
    public void Constructor_NullStage_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            new Sequence<TestSig, TestSig>(null!));
    }

    [Fact]
    public void Constructor3_NullStage1_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            new Sequence<TestSig, ScoreSig, TestSig>(null!, new ScoreToAnswer()));
    }

    [Fact]
    public void Constructor3_NullStage2_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            new Sequence<TestSig, ScoreSig, TestSig>(new QuestionToAnswer(), null!));
    }

    [Fact]
    public async Task InputMutation_PropagatesThroughChain()
    {
        var seq = new Sequence<TestSig, TestSig, TestSig, TestSig>(
            new AppendToAnswer("!"),
            new AppendToAnswer("?"),
            new AppendToAnswer("."));

        var result = await seq.ForwardAsync(new TestSig { Question = "q" }, TestContext.Current.CancellationToken);

        result.Value.Answer.ShouldBe("!?.");
    }

    [Fact]
    public async Task Trace_ContainsSequenceMetadata()
    {
        var seq = new Sequence<TestSig, ScoreSig, TestSig>(
            new QuestionToAnswer(), new ScoreToAnswer());

        var result = await seq.ForwardAsync(new TestSig { Question = "x" }, TestContext.Current.CancellationToken);

        result.Trace.Metadata.ShouldContainKey(SignalGrid.Ampelum.Composition.TraceKeys.CompositionPrimitive);
        result.Trace.Metadata[SignalGrid.Ampelum.Composition.TraceKeys.CompositionPrimitive].ShouldBe("Sequence");
    }
}

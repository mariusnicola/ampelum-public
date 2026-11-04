namespace Ampelum.Tests.Orchestration;

using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Composition;
using SignalGrid.Ampelum.Orchestration;
using Ampelum.Tests.Composition._Sigs;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

public sealed class LoopTests
{
    static TraceRecord Dummy(string modName, object? output = null) => new(
        Id: Guid.NewGuid(),
        ModuleName: modName,
        SignatureType: typeof(object),
        Inputs: new Dictionary<string, object?>(),
        Outputs: output is not null
            ? new Dictionary<string, object?> { ["v"] = output }
            : new Dictionary<string, object?>(),
        PromptMessages: [],
        RawResponse: "",
        Usage: null,
        Duration: TimeSpan.Zero,
        Error: null,
        Metadata: new Dictionary<string, object?>());

    static Prediction<T> Pred<T>(T value, string mod = "m") =>
        new(value, Dummy(mod, value));

    sealed class CountdownPredicate(int breakAfter) : IModule<TestSig, LoopPredicateResult<TestSig>>
    {
        int _counter;
        public Task<Prediction<LoopPredicateResult<TestSig>>> ForwardAsync(TestSig inputs, CancellationToken ct)
        {
            _counter++;
            var shouldContinue = _counter < breakAfter;
            return Task.FromResult(
                Pred(new LoopPredicateResult<TestSig>(shouldContinue, shouldContinue ? null : "countdown"), "pred"));
        }
    }

    sealed class AlwaysContinue : IModule<TestSig, LoopPredicateResult<TestSig>>
    {
        public Task<Prediction<LoopPredicateResult<TestSig>>> ForwardAsync(TestSig inputs, CancellationToken ct) =>
            Task.FromResult(Pred(new LoopPredicateResult<TestSig>(true), "pred"));
    }

    sealed class CountingContinue : IModule<TestSig, LoopPredicateResult<TestSig>>
    {
        public int Calls;
        public Task<Prediction<LoopPredicateResult<TestSig>>> ForwardAsync(TestSig inputs, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Pred(new LoopPredicateResult<TestSig>(true), "pred"));
        }
    }

    sealed class AlwaysBreak : IModule<TestSig, LoopPredicateResult<TestSig>>
    {
        public Task<Prediction<LoopPredicateResult<TestSig>>> ForwardAsync(TestSig inputs, CancellationToken ct) =>
            Task.FromResult(Pred(new LoopPredicateResult<TestSig>(false, "break_immediate"), "pred"));
    }

    sealed class BreakWithReason(string reason) : IModule<TestSig, LoopPredicateResult<TestSig>>
    {
        public Task<Prediction<LoopPredicateResult<TestSig>>> ForwardAsync(TestSig inputs, CancellationToken ct) =>
            Task.FromResult(Pred(new LoopPredicateResult<TestSig>(false, reason), "pred"));
    }

    sealed class CountingBody : IModule<TestSig, TestSig>
    {
        public Task<Prediction<TestSig>> ForwardAsync(TestSig inputs, CancellationToken ct)
        {
            var next = inputs with
            {
                Answer = (int.TryParse(inputs.Answer, out var n) ? n + 1 : 1).ToString()
            };
            return Task.FromResult(Pred(next, "body"));
        }
    }

    sealed class TrackedBody : IModule<TestSig, TestSig>
    {
        public int CallCount;
        public Task<Prediction<TestSig>> ForwardAsync(TestSig inputs, CancellationToken ct)
        {
            CallCount++;
            var next = inputs with { Answer = CallCount.ToString() };
            return Task.FromResult(Pred(next, "body"));
        }
    }

    sealed class ThrowingPredicate : IModule<TestSig, LoopPredicateResult<TestSig>>
    {
        public Task<Prediction<LoopPredicateResult<TestSig>>> ForwardAsync(TestSig inputs, CancellationToken ct) =>
            throw new InvalidOperationException("pred_fail");
    }

    sealed class ThrowingBody : IModule<TestSig, TestSig>
    {
        public Task<Prediction<TestSig>> ForwardAsync(TestSig inputs, CancellationToken ct) =>
            throw new InvalidOperationException("body_fail");
    }

    [Fact]
    public async Task HappyPath_IteratesThenBreaks()
    {
        var loop = new Loop<TestSig>(new CountdownPredicate(3), new CountingBody());

        var result = await loop.ForwardAsync(new TestSig { Question = "q" }, TestContext.Current.CancellationToken);

        result.Value.Iterations.ShouldBe(2);
        result.Value.FinalState.Answer.ShouldBe("2");
        result.Value.Reason.ShouldBe("countdown");
    }

    [Fact]
    public async Task AStopOnTheCheckAfterTheLastAllowedRun_KeepsThePredicatesReason()
    {
        var body = new TrackedBody();
        var loop = new Loop<TestSig>(new CountdownPredicate(3), body, maxIterations: 2);

        var result = await loop.ForwardAsync(new TestSig { Question = "q" }, TestContext.Current.CancellationToken);

        body.CallCount.ShouldBe(2);
        result.Value.Iterations.ShouldBe(2);
        result.Value.Reason.ShouldBe("countdown");
        result.Trace.Metadata[SignalGrid.Ampelum.Composition.TraceKeys.LoopConverged].ShouldBe(true);
        result.Trace.Metadata[SignalGrid.Ampelum.Composition.TraceKeys.LoopIterations].ShouldBe(2);
    }

    [Fact]
    public async Task APredicateThatNeverStops_RunsTheBodyMaxIterationsTimes_AndReportsMaxIterations()
    {
        var body = new TrackedBody();
        var loop = new Loop<TestSig>(new AlwaysContinue(), body, maxIterations: 3);

        var result = await loop.ForwardAsync(new TestSig { Question = "q" }, TestContext.Current.CancellationToken);

        body.CallCount.ShouldBe(3);
        result.Value.Iterations.ShouldBe(3);
        result.Value.Reason.ShouldBe("max_iterations");
        result.Trace.Metadata[SignalGrid.Ampelum.Composition.TraceKeys.LoopConverged].ShouldBe(false);
    }

    [Fact]
    public async Task MaxIterations_StopsAtLimit()
    {
        var loop = new Loop<TestSig>(new AlwaysContinue(), new CountingBody(), maxIterations: 5);

        var result = await loop.ForwardAsync(new TestSig { Question = "q" }, TestContext.Current.CancellationToken);

        result.Value.Iterations.ShouldBe(5);
        result.Value.Reason.ShouldBe("max_iterations");
    }

    [Fact]
    public async Task APredicateThatNeverStops_IsCalledMaxIterationsPlusOneTimes()
    {
        var predicate = new CountingContinue();
        var loop = new Loop<TestSig>(predicate, new CountingBody(), maxIterations: 4);

        await loop.ForwardAsync(new TestSig { Question = "q" }, TestContext.Current.CancellationToken);

        predicate.Calls.ShouldBe(5);
    }

    [Fact]
    public async Task APredicateThatStopsWithTheReasonMaxIterations_IsReadAsNotConverged()
    {
        var loop = new Loop<TestSig>(new BreakWithReason("max_iterations"), new CountingBody());

        var result = await loop.ForwardAsync(new TestSig { Question = "q" }, TestContext.Current.CancellationToken);

        result.Value.Reason.ShouldBe("max_iterations");
        result.Trace.Metadata[SignalGrid.Ampelum.Composition.TraceKeys.LoopConverged].ShouldBe(false);
    }

    [Fact]
    public async Task PredicateError_PropagatesException()
    {
        var loop = new Loop<TestSig>(new ThrowingPredicate(), new CountingBody());

        await Should.ThrowAsync<InvalidOperationException>(() =>
            loop.ForwardAsync(new TestSig { Question = "q" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BodyError_PropagatesException()
    {
        var loop = new Loop<TestSig>(new AlwaysContinue(), new ThrowingBody());

        await Should.ThrowAsync<InvalidOperationException>(() =>
            loop.ForwardAsync(new TestSig { Question = "q" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StateMutation_IterationReceivesPreviousOutput()
    {
        var body = new TrackedBody();
        var loop = new Loop<TestSig>(new CountdownPredicate(3), body);

        var result = await loop.ForwardAsync(new TestSig { Question = "q" }, TestContext.Current.CancellationToken);

        result.Value.Iterations.ShouldBe(2);
        result.Value.FinalState.Answer.ShouldBe("2");
        body.CallCount.ShouldBe(2);
    }

    [Fact]
    public async Task SingleIteration_BreaksImmediately()
    {
        var body = new TrackedBody();
        var loop = new Loop<TestSig>(new AlwaysBreak(), body);

        var result = await loop.ForwardAsync(new TestSig { Question = "q" }, TestContext.Current.CancellationToken);

        result.Value.Iterations.ShouldBe(0);
        result.Value.Reason.ShouldBe("break_immediate");
        body.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task CancelledToken_ThrowsOperationCanceled()
    {
        var loop = new Loop<TestSig>(new AlwaysContinue(), new CountingBody(), maxIterations: 100);

        await Should.ThrowAsync<OperationCanceledException>(() =>
            loop.ForwardAsync(new TestSig { Question = "q" }, new CancellationToken(canceled: true)));
    }

    [Fact]
    public void Constructor_NullPredicate_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            new Loop<TestSig>(null!, new CountingBody()));
    }

    [Fact]
    public void Constructor_NullBody_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            new Loop<TestSig>(new AlwaysBreak(), null!));
    }

    [Fact]
    public void Constructor_ZeroMaxIterations_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new Loop<TestSig>(new AlwaysBreak(), new CountingBody(), maxIterations: 0));
    }

    [Fact]
    public async Task CustomReason_FromPredicate()
    {
        var loop = new Loop<TestSig>(new BreakWithReason("converged"), new CountingBody());

        var result = await loop.ForwardAsync(new TestSig { Question = "q" }, TestContext.Current.CancellationToken);

        result.Value.Reason.ShouldBe("converged");
    }

    [Fact]
    public async Task Trace_ContainsLoopMetadata()
    {
        var loop = new Loop<TestSig>(new CountdownPredicate(3), new CountingBody());

        var result = await loop.ForwardAsync(new TestSig { Question = "q" }, TestContext.Current.CancellationToken);

        result.Trace.Metadata.ShouldContainKey(SignalGrid.Ampelum.Composition.TraceKeys.CompositionPrimitive);
        result.Trace.Metadata[SignalGrid.Ampelum.Composition.TraceKeys.CompositionPrimitive].ShouldBe("Loop");
        result.Trace.ModuleName.ShouldBe("Loop<TestSig>");
    }
}

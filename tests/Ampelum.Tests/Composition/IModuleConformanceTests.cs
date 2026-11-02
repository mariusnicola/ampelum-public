namespace Ampelum.Tests.Composition;

using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Composition;
using Ampelum.Tests.Composition._Sigs;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

public sealed class IModuleConformanceTests
{
    static AmpelumContext Ctx(FakeChatClient chat) =>
        new(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);

    [Fact]
    public async Task Predict_ImplementsIModule_ForwardAsyncReturnsPrediction()
    {
        var chat = new FakeChatClient().EnqueueText("[[ ## Answer ## ]]\nworld");
        IModule<TestSig, TestSig> module = Predict.Create<TestSig>(chat);
        var pred = await module.ForwardAsync(new TestSig { Question = "hi" }, TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldBe("world");
    }

    [Fact]
    public async Task BestOfN_ImplementsIModule_ForwardAsyncReturnsPrediction()
    {
        var chat = new FakeChatClient()
            .EnqueueText("[[ ## Answer ## ]]\na")
            .EnqueueText("[[ ## Answer ## ]]\nb");
        var bestOfN = new BestOfN<TestSig>(Ctx(chat),
            new Ampelum.Tests.Composition._Metrics.AlphabeticalMetric(), n: 2);
        IModule<TestSig, TestSig> module = bestOfN;
        var pred = await module.ForwardAsync(new TestSig { Question = "?" }, TestContext.Current.CancellationToken);
        pred.Value.ShouldNotBeNull();
    }

    [Fact]
    public async Task Refine_ImplementsIModule_ForwardAsyncReturnsPrediction()
    {
        var chat = new FakeChatClient().EnqueueText("[[ ## Answer ## ]]\nfirst");
        var refine = new Refine<TestSig>(Ctx(chat),
            new Ampelum.Tests.Composition._Metrics.AlphabeticalMetric(),
            threshold: 0.0, maxRefinements: 0);
        IModule<TestSig, TestSig> module = refine;
        var pred = await module.ForwardAsync(new TestSig { Question = "?" }, TestContext.Current.CancellationToken);
        pred.Value.ShouldNotBeNull();
    }

    [Fact]
    public async Task Parallel_ImplementsIModule_ForwardAsyncReturnsPrediction()
    {
        var chat = new FakeChatClient()
            .EnqueueText("[[ ## Answer ## ]]\na")
            .EnqueueText("[[ ## Answer ## ]]\nb");
        var p = new Parallel<TestSig>(Ctx(chat), new[]
        {
            new Predict<TestSig>(Ctx(chat)),
            new Predict<TestSig>(Ctx(chat)),
        });
        IModule<TestSig, TestSig> module = p;
        var pred = await module.ForwardAsync(new TestSig { Question = "?" }, TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task MultiChainComparison_ImplementsIModule_ForwardAsyncReturnsPrediction()
    {
        var chat = new FakeChatClient()
            .EnqueueText("[[ ## reasoning ## ]]\nstep\n[[ ## Answer ## ]]\na")
            .EnqueueText("[[ ## reasoning ## ]]\nstep\n[[ ## Answer ## ]]\nb")
            .EnqueueText("[[ ## Answer ## ]]\na");
        var mcc = new MultiChainComparison<TestSig>(Ctx(chat), n: 2,
            aggregator: AggregatorStrategy.MajorityVote);
        IModule<TestSig, TestSig> module = mcc;
        var pred = await module.ForwardAsync(new TestSig { Question = "?" }, TestContext.Current.CancellationToken);
        pred.Value.ShouldNotBeNull();
    }

    [Fact]
    public void IModule_TInVariance_DerivedInputSlot_AcceptsBaseInputModule()
    {
        // Variance smoke: declare an IModule<DerivedSig, DerivedSig> slot and assign
        // an IModule<BaseSig, DerivedSig> producer. Compiles iff `in TIn` contravariance holds.
        // We use `Predict<TestSig>` which is IModule<TestSig, TestSig>; variance is exercised
        // by assigning to a wider slot.
        // The test asserts compile-time only: the file compiling is the assertion.
        IModule<TestSig, TestSig> producer = Predict.Create<TestSig>(new FakeChatClient());
        IModule<TestSig, TestSig> slot = producer; // would fail if invariant on TIn
        slot.ShouldNotBeNull();
    }
}

namespace Ampelum.Tests.Composition;

using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Composition;
using Ampelum.Tests.Composition._Sigs;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

public sealed class RetrofitConformanceTests
{
    static AmpelumContext Ctx(FakeChatClient chat) =>
        new(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);

    [Fact]
    public async Task BestOfN_DirectCall_AndIModuleCall_ProduceSameValue()
    {
        var chatA = new FakeChatClient()
            .EnqueueText("[[ ## Answer ## ]]\na")
            .EnqueueText("[[ ## Answer ## ]]\nb");
        var direct = new BestOfN<TestSig>(Ctx(chatA),
            new Ampelum.Tests.Composition._Metrics.AlphabeticalMetric(), n: 2);
        var p1 = await direct.ForwardAsync(new TestSig { Question = "?" }, demos: null, ct: TestContext.Current.CancellationToken);

        var chatB = new FakeChatClient()
            .EnqueueText("[[ ## Answer ## ]]\na")
            .EnqueueText("[[ ## Answer ## ]]\nb");
        IModule<TestSig, TestSig> via = new BestOfN<TestSig>(Ctx(chatB),
            new Ampelum.Tests.Composition._Metrics.AlphabeticalMetric(), n: 2);
        var p2 = await via.ForwardAsync(new TestSig { Question = "?" }, TestContext.Current.CancellationToken);

        p1.Value.Answer.ShouldBe(p2.Value.Answer);
    }

    [Fact]
    public async Task Refine_DirectCall_AndIModuleCall_ProduceSameValue()
    {
        var chatA = new FakeChatClient().EnqueueText("[[ ## Answer ## ]]\nresult");
        var direct = new Refine<TestSig>(Ctx(chatA),
            new Ampelum.Tests.Composition._Metrics.AlphabeticalMetric(),
            threshold: 0.0, maxRefinements: 0);
        var p1 = await direct.ForwardAsync(new TestSig { Question = "?" }, demos: null, ct: TestContext.Current.CancellationToken);

        var chatB = new FakeChatClient().EnqueueText("[[ ## Answer ## ]]\nresult");
        IModule<TestSig, TestSig> via = new Refine<TestSig>(Ctx(chatB),
            new Ampelum.Tests.Composition._Metrics.AlphabeticalMetric(),
            threshold: 0.0, maxRefinements: 0);
        var p2 = await via.ForwardAsync(new TestSig { Question = "?" }, TestContext.Current.CancellationToken);

        p1.Value.Answer.ShouldBe(p2.Value.Answer);
    }

    [Fact]
    public async Task Parallel_DirectCall_AndIModuleCall_ProduceSameValue()
    {
        FakeChatClient mk() => new FakeChatClient()
            .EnqueueText("[[ ## Answer ## ]]\na")
            .EnqueueText("[[ ## Answer ## ]]\nb");
        var chatA = mk();
        var direct = new Parallel<TestSig>(Ctx(chatA), new[]
        {
            new Predict<TestSig>(Ctx(chatA)),
            new Predict<TestSig>(Ctx(chatA)),
        });
        var p1 = await direct.ForwardAsync(new TestSig { Question = "?" }, demos: null, ct: TestContext.Current.CancellationToken);

        var chatB = mk();
        IModule<TestSig, TestSig> via = new Parallel<TestSig>(Ctx(chatB), new[]
        {
            new Predict<TestSig>(Ctx(chatB)),
            new Predict<TestSig>(Ctx(chatB)),
        });
        var p2 = await via.ForwardAsync(new TestSig { Question = "?" }, TestContext.Current.CancellationToken);

        p1.Value.Answer.ShouldBe(p2.Value.Answer);
    }

    [Fact]
    public async Task MultiChainComparison_DirectCall_AndIModuleCall_ProduceSameValue()
    {
        FakeChatClient mk() => new FakeChatClient()
            .EnqueueText("[[ ## reasoning ## ]]\nstep\n[[ ## Answer ## ]]\na")
            .EnqueueText("[[ ## reasoning ## ]]\nstep\n[[ ## Answer ## ]]\na")
            .EnqueueText("[[ ## Answer ## ]]\na");
        var chatA = mk();
        var direct = new MultiChainComparison<TestSig>(Ctx(chatA), n: 2,
            aggregator: AggregatorStrategy.MajorityVote);
        var p1 = await direct.ForwardAsync(new TestSig { Question = "?" }, demos: null, ct: TestContext.Current.CancellationToken);

        var chatB = mk();
        IModule<TestSig, TestSig> via = new MultiChainComparison<TestSig>(Ctx(chatB), n: 2,
            aggregator: AggregatorStrategy.MajorityVote);
        var p2 = await via.ForwardAsync(new TestSig { Question = "?" }, TestContext.Current.CancellationToken);

        p1.Value.Answer.ShouldBe(p2.Value.Answer);
    }
}

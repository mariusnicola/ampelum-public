using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Orchestration;

[Signature("Echo test signature.")]
public sealed record EchoSig : ISignature
{
    [Input] public string Message { get; init; } = "";
    [Output] public string Reply { get; init; } = "";
}

public sealed class LazyTests
{
    [Fact]
    public void NotEvaluated_BeforeFirstCall()
    {
        var ctx = new AmpelumContext(new FakeChatClient(), new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        var lazy = new global::SignalGrid.Ampelum.Orchestration.Lazy<EchoSig>(new Predict<EchoSig>(ctx));
        lazy.IsEvaluated.ShouldBeFalse();
        lazy.Cached.ShouldBeNull();
    }

    [Fact]
    public async Task Evaluated_AfterFirstCall()
    {
        var client = new FakeChatClient();
        client.EnqueueText("[[ ## Reply ## ]]\nhello back");
        var ctx = new AmpelumContext(client, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        var lazy = new global::SignalGrid.Ampelum.Orchestration.Lazy<EchoSig>(new Predict<EchoSig>(ctx));
        await lazy.ForwardAsync(new EchoSig { Message = "hello" }, TestContext.Current.CancellationToken);
        lazy.IsEvaluated.ShouldBeTrue();
        lazy.Cached.ShouldNotBeNull();
    }

    [Fact]
    public async Task SecondCall_Returns_SameCachedResult()
    {
        var client = new FakeChatClient();
        client.EnqueueText("[[ ## Reply ## ]]\nhello back");
        var ctx = new AmpelumContext(client, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        var lazy = new global::SignalGrid.Ampelum.Orchestration.Lazy<EchoSig>(new Predict<EchoSig>(ctx));
        var r1 = await lazy.ForwardAsync(new EchoSig { Message = "first" }, TestContext.Current.CancellationToken);
        var r2 = await lazy.ForwardAsync(new EchoSig { Message = "second" }, TestContext.Current.CancellationToken);
        r1.Value.ShouldBe(r2.Value);
        r1.Trace.ShouldBe(r2.Trace);
    }

    [Fact]
    public async Task InnerModule_Called_OnlyOnce()
    {
        var calls = 0;
        var counting = new CountingModule<EchoSig>(_ => calls++);
        var lazy = new global::SignalGrid.Ampelum.Orchestration.Lazy<EchoSig>(counting);
        await lazy.ForwardAsync(new EchoSig { Message = "a" }, TestContext.Current.CancellationToken);
        await lazy.ForwardAsync(new EchoSig { Message = "b" }, TestContext.Current.CancellationToken);
        await lazy.ForwardAsync(new EchoSig { Message = "c" }, TestContext.Current.CancellationToken);
        calls.ShouldBe(1);
    }

    [Fact]
    public void Constructor_Throws_OnNull()
    {
        Should.Throw<ArgumentNullException>(() => new global::SignalGrid.Ampelum.Orchestration.Lazy<EchoSig>(null!));
    }

    private sealed class CountingModule<TSig> : IModule<TSig, TSig> where TSig : ISignature
    {
        readonly Action<int> _onCall;
        int _calls;
        public CountingModule(Action<int> onCall) => _onCall = onCall;
        public Task<Prediction<TSig>> ForwardAsync(TSig inputs, CancellationToken ct = default)
        {
            _onCall(++_calls);
            return Task.FromResult(new Prediction<TSig>(inputs, new TraceRecord(
                Guid.NewGuid(), "Counting", typeof(TSig),
                new Dictionary<string, object?>(), new Dictionary<string, object?>(),
                [], "", null, TimeSpan.Zero, null, new Dictionary<string, object?>())));
        }
    }
}
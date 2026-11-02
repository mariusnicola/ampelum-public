using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Orchestration;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Orchestration;

[Signature("Test sig for ErrorHandler.")]
public sealed record TestHandlerSig : ISignature
{
    [Input] public string Prompt { get; init; } = "";
    [Output] public string Reply { get; init; } = "";
}

public sealed class ErrorHandlerTests
{
    [Fact]
    public async Task PassThrough_OnSuccess()
    {
        var ok = new FixedModule<TestHandlerSig>(new TestHandlerSig { Reply = "success" });
        var handler = new global::SignalGrid.Ampelum.Orchestration.ErrorHandler<TestHandlerSig>(ok, new global::SignalGrid.Ampelum.Orchestration.ErrorHandler<TestHandlerSig>.ErrorHandlerConfig { MaxRetries = 2 });
        var result = await handler.ForwardAsync(new TestHandlerSig { Prompt = "hi" }, TestContext.Current.CancellationToken);
        result.Value.Reply.ShouldBe("success");
    }

    [Fact]
    public async Task Retries_Once_ThenSucceeds()
    {
        var retries = 0;
        var flaky = new FlakyModule<TestHandlerSig>(() =>
        {
            retries++;
            if (retries < 2) throw new InvalidOperationException("flaky");
            return new TestHandlerSig { Reply = "recovered" };
        });
        var onRetryCalls = 0;
        var handler = new global::SignalGrid.Ampelum.Orchestration.ErrorHandler<TestHandlerSig>(flaky, new global::SignalGrid.Ampelum.Orchestration.ErrorHandler<TestHandlerSig>.ErrorHandlerConfig
        {
            MaxRetries = 2,
            RetryDelay = TimeSpan.Zero,
            OnRetry = (ex, attempt) => onRetryCalls++,
        });
        var result = await handler.ForwardAsync(new TestHandlerSig(), TestContext.Current.CancellationToken);
        result.Value.Reply.ShouldBe("recovered");
        retries.ShouldBe(2);
        onRetryCalls.ShouldBe(1);
    }

    [Fact]
    public async Task FallsBack_AfterExhaustedRetries()
    {
        var alwaysFail = new ThrowingModule<TestHandlerSig>(new InvalidOperationException("boom"));
        var handler = new global::SignalGrid.Ampelum.Orchestration.ErrorHandler<TestHandlerSig>(alwaysFail, new global::SignalGrid.Ampelum.Orchestration.ErrorHandler<TestHandlerSig>.ErrorHandlerConfig
        {
            MaxRetries = 1,
            RetryDelay = TimeSpan.Zero,
            FallbackOutput = _ => new TestHandlerSig { Reply = "fallback" },
        });
        var result = await handler.ForwardAsync(new TestHandlerSig(), TestContext.Current.CancellationToken);
        result.Value.Reply.ShouldBe("fallback");
        result.Trace.Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task Rethrows_WhenNoFallback()
    {
        var alwaysFail = new ThrowingModule<TestHandlerSig>(new InvalidOperationException("boom"));
        var handler = new global::SignalGrid.Ampelum.Orchestration.ErrorHandler<TestHandlerSig>(alwaysFail, new global::SignalGrid.Ampelum.Orchestration.ErrorHandler<TestHandlerSig>.ErrorHandlerConfig
        {
            MaxRetries = 0,
            RetryDelay = TimeSpan.Zero,
        });
        await Should.ThrowAsync<OperationCanceledException>(() => handler.ForwardAsync(new TestHandlerSig()));
    }

    [Fact]
    public async Task IsRetryable_Filter_Skips_NonMatching()
    {
        var retries = 0;
        var flaky = new FlakyModule<TestHandlerSig>(() =>
        {
            retries++;
            if (retries < 2) throw new ArgumentException("not retryable");
            return new TestHandlerSig { Reply = "ok" };
        });
        var handler = new global::SignalGrid.Ampelum.Orchestration.ErrorHandler<TestHandlerSig>(flaky, new global::SignalGrid.Ampelum.Orchestration.ErrorHandler<TestHandlerSig>.ErrorHandlerConfig
        {
            MaxRetries = 2,
            RetryDelay = TimeSpan.Zero,
            IsRetryable = ex => ex is InvalidOperationException, // ArgumentException won't match
        });
        await Should.ThrowAsync<ArgumentException>(() => handler.ForwardAsync(new TestHandlerSig()));
        retries.ShouldBe(1); // first attempt threw, not retried
    }

    private sealed class FixedModule<TSig>(TSig output) : IModule<TSig, TSig> where TSig : ISignature
    {
        public Task<Prediction<TSig>> ForwardAsync(TSig inputs, CancellationToken ct = default)
            => Task.FromResult(new Prediction<TSig>(output, new TraceRecord(
                Guid.NewGuid(), "Fixed", typeof(TSig),
                new Dictionary<string, object?>(), new Dictionary<string, object?>(),
                [], "", null, TimeSpan.Zero, null, new Dictionary<string, object?>())));
    }

    private sealed class ThrowingModule<TSig>(Exception toThrow) : IModule<TSig, TSig> where TSig : ISignature
    {
        public Task<Prediction<TSig>> ForwardAsync(TSig inputs, CancellationToken ct = default)
            => throw toThrow;
    }

    private sealed class FlakyModule<TSig>(Func<TSig> factory) : IModule<TSig, TSig> where TSig : ISignature
    {
        public Task<Prediction<TSig>> ForwardAsync(TSig inputs, CancellationToken ct = default)
        {
            var output = factory();
            return Task.FromResult(new Prediction<TSig>(output, new TraceRecord(
                Guid.NewGuid(), "Flaky", typeof(TSig),
                new Dictionary<string, object?>(), new Dictionary<string, object?>(),
                [], "", null, TimeSpan.Zero, null, new Dictionary<string, object?>())));
        }
    }
}
using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.Resilience;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Polly.CircuitBreaker;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Resilience;

[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class CircuitBreakerChatClientTests
{
    static IList<ChatMessage> Msgs() => [new ChatMessage(ChatRole.User, "hi")];

    [Fact]
    public void Ctor_NullInner_Throws() =>
        Should.Throw<ArgumentNullException>(() => new CircuitBreakerChatClient(null!, new CircuitBreakerOptions()));

    [Fact]
    public void Ctor_NullOptions_Throws()
    {
        var inner = new FakeChatClient();
        Should.Throw<ArgumentNullException>(() => new CircuitBreakerChatClient(inner, null!));
    }

    [Fact]
    public async Task GetResponseAsync_Success_Forwards()
    {
        var inner = new FakeChatClient().EnqueueText("ok");
        var cb = new CircuitBreakerChatClient(inner, new CircuitBreakerOptions());
        var resp = await cb.GetResponseAsync(Msgs(), null, TestContext.Current.CancellationToken);
        resp.Text.ShouldBe("ok");
        cb.Dispose();
    }

    [Fact]
    public async Task GetResponseAsync_NonHandledError_Propagates()
    {
        var inner = new FakeChatClient().EnqueueError(new InvalidOperationException("boom"));
        var cb = new CircuitBreakerChatClient(inner, new CircuitBreakerOptions());
        await Should.ThrowAsync<InvalidOperationException>(() => cb.GetResponseAsync(Msgs(), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetResponseAsync_TripsAfterThreshold_RejectsWithBrokenCircuit()
    {
        var inner = new FakeChatClient();
        for (var i = 0; i < 10; i++) inner.EnqueueError(new HttpRequestException("5xx"));
        inner.EnqueueText("recovered");
        var cb = new CircuitBreakerChatClient(inner, new CircuitBreakerOptions
        {
            FailureThreshold = 3,
            BreakDuration = TimeSpan.FromSeconds(1),
        });
        var ct = TestContext.Current.CancellationToken;

        // Drive enough failures to trip the breaker.
        for (var i = 0; i < 3; i++)
            await Should.ThrowAsync<HttpRequestException>(() => cb.GetResponseAsync(Msgs(), null, ct));
        // While Open: subsequent calls short-circuit with BrokenCircuitException.
        await Should.ThrowAsync<BrokenCircuitException>(() => cb.GetResponseAsync(Msgs(), null, ct));
    }

    [Fact]
    public async Task GetResponseAsync_RateLimitException_TripsBreaker()
    {
        // Use a custom exception whose name contains "RateLimit" — default predicate trips on it.
        var inner = new FakeChatClient();
        for (var i = 0; i < 10; i++) inner.EnqueueError(new MyRateLimitException());
        var cb = new CircuitBreakerChatClient(inner, new CircuitBreakerOptions
        {
            FailureThreshold = 2,
            BreakDuration = TimeSpan.FromSeconds(1),
        });
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < 2; i++)
            await Should.ThrowAsync<MyRateLimitException>(() => cb.GetResponseAsync(Msgs(), null, ct));
        await Should.ThrowAsync<BrokenCircuitException>(() => cb.GetResponseAsync(Msgs(), null, ct));
    }

    [Fact]
    public async Task GetStreamingResponseAsync_Success_Yields()
    {
        var inner = new FakeChatClient().EnqueueStreamingChunks("a", "b");
        var cb = new CircuitBreakerChatClient(inner, new CircuitBreakerOptions());
        var ct = TestContext.Current.CancellationToken;
        var chunks = new List<string>();
        await foreach (var u in cb.GetStreamingResponseAsync(Msgs(), null, ct))
            chunks.Add(u.Text ?? "");
        string.Concat(chunks).ShouldBe("ab");
    }

    [Fact]
    public void GetService_ReturnsMetadataAndSelf()
    {
        var inner = new FakeChatClient();
        var cb = new CircuitBreakerChatClient(inner, new CircuitBreakerOptions());
        (cb.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata)!.ProviderName.ShouldBe(nameof(CircuitBreakerChatClient));
        cb.GetService(typeof(IChatClient)).ShouldBe(cb);
        cb.GetService(typeof(string)).ShouldBeNull();
        Should.Throw<ArgumentNullException>(() => cb.GetService(null!));
    }

    [Fact]
    public void DefaultShouldTrip_DoesNotTripOnUnrelatedException()
    {
        var opts = new CircuitBreakerOptions();
        opts.ShouldTrip(new InvalidOperationException()).ShouldBeFalse();
        opts.ShouldTrip(new HttpRequestException("x")).ShouldBeTrue();
        opts.ShouldTrip(new MyRateLimitException()).ShouldBeTrue();
    }

    [Fact]
    public void Options_RecordEquality()
    {
        var a = new CircuitBreakerOptions();
        var b = new CircuitBreakerOptions { FailureThreshold = a.FailureThreshold, BreakDuration = a.BreakDuration, ShouldTrip = a.ShouldTrip };
        a.FailureThreshold.ShouldBe(5);
        a.BreakDuration.ShouldBe(TimeSpan.FromSeconds(30));
        a.Equals(b).ShouldBeTrue();
    }

    sealed class MyRateLimitException : Exception
    {
        public MyRateLimitException() : base("rate limited") { }
    }
}

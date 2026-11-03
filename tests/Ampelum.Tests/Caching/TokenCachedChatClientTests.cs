using Microsoft.Extensions.AI;
using Shouldly;
using SignalGrid.Ampelum.Caching;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace Ampelum.Tests.Caching;

public sealed class TokenCachedChatClientTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    static readonly ChatMessage[] Hello = [new(ChatRole.User, "Opening hours?")];

    [Fact]
    public async Task A_repeated_identical_request_does_not_reach_the_inner_client()
    {
        var inner = new FakeChatClient().EnqueueText("9 to 17.").EnqueueText("unused");
        using var cache = new TokenCache();
        var sut = new TokenCachedChatClient(inner, cache);

        var first = await sut.GetResponseAsync(Hello, cancellationToken: Ct);
        var second = await sut.GetResponseAsync(Hello, cancellationToken: Ct);

        inner.RecordedCalls.Count.ShouldBe(1);
        second.Text.ShouldBe(first.Text);
        cache.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_different_temperature_reaches_the_inner_client_again()
    {
        var inner = new FakeChatClient().EnqueueText("cold").EnqueueText("warm");
        using var cache = new TokenCache();
        var sut = new TokenCachedChatClient(inner, cache);

        var cold = await sut.GetResponseAsync(Hello, new ChatOptions { Temperature = 0f }, Ct);
        var warm = await sut.GetResponseAsync(Hello, new ChatOptions { Temperature = 1f }, Ct);
        var coldAgain = await sut.GetResponseAsync(Hello, new ChatOptions { Temperature = 0f }, Ct);

        inner.RecordedCalls.Count.ShouldBe(2);
        cold.Text.ShouldBe("cold");
        warm.Text.ShouldBe("warm");
        coldAgain.Text.ShouldBe("cold");
    }

    [Fact]
    public async Task Streaming_passes_through_uncached()
    {
        var inner = new FakeChatClient().EnqueueStreamingChunks("a").EnqueueStreamingChunks("b");
        using var cache = new TokenCache();
        var sut = new TokenCachedChatClient(inner, cache);

        await foreach (var _ in sut.GetStreamingResponseAsync(Hello, cancellationToken: Ct)) { }
        await foreach (var _ in sut.GetStreamingResponseAsync(Hello, cancellationToken: Ct)) { }

        inner.RecordedCalls.Count.ShouldBe(2);
        cache.Count.ShouldBe(0);
    }

    [Fact]
    public void A_null_cache_is_refused() =>
        Should.Throw<ArgumentNullException>(() => new TokenCachedChatClient(new FakeChatClient(), null!));
}

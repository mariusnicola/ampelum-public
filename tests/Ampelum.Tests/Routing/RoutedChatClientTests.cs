using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using SignalGrid.Ampelum.Routing;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;
using SignalGrid.Ampelum;

namespace Ampelum.Tests.Routing;

[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class RoutedChatClientTests
{
    static IList<ChatMessage> Msgs() => [new ChatMessage(ChatRole.User, "hi")];

    [Fact]
    public async Task GetResponseAsync_PrimarySucceeds_StampsTargetName()
    {
        var inner = new FakeChatClient().EnqueueText("[[ ## A ## ]]\nhello");
        var router = new RoutedChatClient(
            [new ProviderEndpoint("primary", inner)],
            new Failover());
        var ct = TestContext.Current.CancellationToken;

        var resp = await router.GetResponseAsync(Msgs(), null, ct);

        resp.AdditionalProperties.ShouldNotBeNull();
        resp.AdditionalProperties![HookMetadataKeys.RouterTargetName].ShouldBe("primary");
    }

    [Fact]
    public async Task GetResponseAsync_PrimaryFails_FailsOverToFallback()
    {
        var failing = new FakeChatClient().EnqueueError(new HttpRequestException("boom"));
        var ok = new FakeChatClient().EnqueueText("[[ ## A ## ]]\nfallback-served");
        var router = new RoutedChatClient(
            [new ProviderEndpoint("a", failing), new ProviderEndpoint("b", ok)],
            new Failover());
        var ct = TestContext.Current.CancellationToken;

        var resp = await router.GetResponseAsync(Msgs(), null, ct);

        resp.AdditionalProperties![HookMetadataKeys.RouterTargetName].ShouldBe("b");
    }

    [Fact]
    public async Task GetResponseAsync_AllFail_RethrowsLastError()
    {
        var a = new FakeChatClient().EnqueueError(new HttpRequestException("a-fail"));
        var b = new FakeChatClient().EnqueueError(new InvalidOperationException("b-fail"));
        var router = new RoutedChatClient(
            [new ProviderEndpoint("a", a), new ProviderEndpoint("b", b)],
            new Failover());
        var ct = TestContext.Current.CancellationToken;

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            router.GetResponseAsync(Msgs(), null, ct));
        ex.Message.ShouldBe("b-fail");
    }

    [Fact]
    public async Task GetResponseAsync_Cancelled_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var inner = new CancellingClient();
        var router = new RoutedChatClient(
            [new ProviderEndpoint("a", inner)],
            new Failover());

        await Should.ThrowAsync<OperationCanceledException>(() =>
            router.GetResponseAsync(Msgs(), null, cts.Token));
    }

    sealed class CancellingClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new(nameof(CancellingClient));
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "x")));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public void Constructor_NullEndpoints_Throws() =>
        Should.Throw<ArgumentNullException>(() => new RoutedChatClient(null!, new Failover()));

    [Fact]
    public void Constructor_NullStrategy_Throws() =>
        Should.Throw<ArgumentNullException>(() =>
            new RoutedChatClient([new ProviderEndpoint("a", new FakeChatClient())], null!));

    [Fact]
    public void Constructor_EmptyEndpoints_Throws() =>
        Should.Throw<ArgumentException>(() =>
            new RoutedChatClient(Array.Empty<ProviderEndpoint>(), new Failover()));

    [Fact]
    public async Task GetStreamingResponseAsync_DispatchesToPrimary()
    {
        var streamingInner = new StreamingClient(["chunk-1"]);
        var router = new RoutedChatClient(
            [new ProviderEndpoint("primary", streamingInner)],
            new Failover());
        var ct = TestContext.Current.CancellationToken;
        var pieces = new List<string>();
        await foreach (var u in router.GetStreamingResponseAsync(Msgs(), null, ct))
            pieces.Add(u.Text);
        pieces.ShouldContain("chunk-1");
    }

    static async Task<List<string>> Stream(RoutedChatClient router, CancellationToken ct)
    {
        var pieces = new List<string>();
        await foreach (var u in router.GetStreamingResponseAsync(Msgs(), null, ct))
            pieces.Add(u.Text);
        return pieces;
    }

    [Fact]
    public async Task GetStreamingResponseAsync_PrimaryFailsBeforeAnyUpdate_FailsOverToFallback()
    {
        var down = new FakeChatClient(); // no streaming reply queued: the stream throws on its first read
        var ok = new FakeChatClient().EnqueueStreamingChunks("from-", "fallback");
        var router = new RoutedChatClient(
            [new ProviderEndpoint("a", down), new ProviderEndpoint("b", ok)],
            new Failover());

        var pieces = await Stream(router, TestContext.Current.CancellationToken);

        pieces.ShouldBe(["from-", "fallback"]);
        down.RecordedCalls.Count.ShouldBe(1);
        ok.RecordedCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_PrimaryFailsAfterAnUpdateWasSent_ThrowsAndNeverAsksTheFallback()
    {
        var partial = new FailingStreamClient("part-");
        var ok = new FakeChatClient().EnqueueStreamingChunks("never");
        var router = new RoutedChatClient(
            [new ProviderEndpoint("a", partial), new ProviderEndpoint("b", ok)],
            new Failover());
        var pieces = new List<string>();

        await Should.ThrowAsync<HttpRequestException>(async () =>
        {
            await foreach (var u in router.GetStreamingResponseAsync(Msgs(), null, TestContext.Current.CancellationToken))
                pieces.Add(u.Text);
        });

        pieces.ShouldBe(["part-"]);
        ok.RecordedCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetStreamingResponseAsync_AllFail_RethrowsLastError()
    {
        var a = new FakeChatClient();
        var b = new FakeChatClient();
        var router = new RoutedChatClient(
            [new ProviderEndpoint("a", a), new ProviderEndpoint("b", b)],
            new Failover());

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            Stream(router, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("no enqueued streaming chunks");
        a.RecordedCalls.Count.ShouldBe(1);
        b.RecordedCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_Cancelled_PropagatesAndNeverAsksTheFallback()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var first = new StreamingClient(["x"]);
        var ok = new FakeChatClient().EnqueueStreamingChunks("never");
        var router = new RoutedChatClient(
            [new ProviderEndpoint("a", first), new ProviderEndpoint("b", ok)],
            new Failover());

        await Should.ThrowAsync<OperationCanceledException>(() => Stream(router, cts.Token));

        ok.RecordedCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetStreamingResponseAsync_PrimaryStreamEndsWithNoUpdate_FailsOverToFallback()
    {
        var empty = new StreamingClient([]);
        var ok = new FakeChatClient().EnqueueStreamingChunks("from-fallback");
        var router = new RoutedChatClient(
            [new ProviderEndpoint("a", empty), new ProviderEndpoint("b", ok)],
            new Failover());

        var pieces = await Stream(router, TestContext.Current.CancellationToken);

        pieces.ShouldBe(["from-fallback"]);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_LastEndpointStreamEndsWithNoUpdate_ReturnsItEmpty()
    {
        var router = new RoutedChatClient(
            [new ProviderEndpoint("a", new StreamingClient([])), new ProviderEndpoint("b", new StreamingClient([]))],
            new Failover());

        (await Stream(router, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task GetStreamingResponseAsync_DisposeThatThrows_NeverStopsTheFailoverOrReplacesTheError()
    {
        var ok = new FakeChatClient().EnqueueStreamingChunks("from-fallback");
        var router = new RoutedChatClient(
            [new ProviderEndpoint("a", new BrokenStreamClient()), new ProviderEndpoint("b", ok)],
            new Failover());
        (await Stream(router, TestContext.Current.CancellationToken)).ShouldBe(["from-fallback"]);

        var last = new RoutedChatClient(
            [new ProviderEndpoint("a", new BrokenStreamClient())],
            new Failover());
        var ex = await Should.ThrowAsync<HttpRequestException>(() => Stream(last, TestContext.Current.CancellationToken));
        ex.Message.ShouldBe("read failed");
    }

    sealed class BrokenStreamClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new(nameof(BrokenStreamClient));
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => new BrokenStream();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }

        sealed class BrokenStream : IAsyncEnumerable<ChatResponseUpdate>
        {
            public IAsyncEnumerator<ChatResponseUpdate> GetAsyncEnumerator(CancellationToken cancellationToken = default)
                => new Enumerator();

            sealed class Enumerator : IAsyncEnumerator<ChatResponseUpdate>
            {
                public ChatResponseUpdate Current => throw new InvalidOperationException();
                public ValueTask<bool> MoveNextAsync() => throw new HttpRequestException("read failed");
                public ValueTask DisposeAsync() => throw new InvalidOperationException("dispose failed");
            }
        }
    }

    sealed class FailingStreamClient(string firstChunk) : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new(nameof(FailingStreamClient));
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, firstChunk);
            await Task.Yield();
            throw new HttpRequestException("dropped mid-stream");
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public void GetService_KnownTypes_AreResolved()
    {
        var router = new RoutedChatClient(
            [new ProviderEndpoint("a", new FakeChatClient())],
            new Failover());
        router.GetService(typeof(ChatClientMetadata)).ShouldBeOfType<ChatClientMetadata>();
        router.GetService(typeof(RoutedChatClient)).ShouldBeSameAs(router);
        router.GetService(typeof(string)).ShouldBeNull();
    }

    [Fact]
    public void GetService_NullType_Throws()
    {
        var router = new RoutedChatClient(
            [new ProviderEndpoint("a", new FakeChatClient())],
            new Failover());
        Should.Throw<ArgumentNullException>(() => router.GetService(null!));
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var router = new RoutedChatClient(
            [new ProviderEndpoint("a", new FakeChatClient())],
            new Failover());
        Should.NotThrow(() => router.Dispose());
    }

    [Fact]
    public async Task GetResponseAsync_PreservesExistingAdditionalProperties()
    {
        var prebuilt = new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## A ## ]]\nx"))
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["upstream.kept"] = 1 }
        };
        var router = new RoutedChatClient(
            [new ProviderEndpoint("p", new RawClient(prebuilt))],
            new Failover());
        var resp = await router.GetResponseAsync(Msgs(), null, TestContext.Current.CancellationToken);
        resp.AdditionalProperties!["upstream.kept"].ShouldBe(1);
        resp.AdditionalProperties[HookMetadataKeys.RouterTargetName].ShouldBe("p");
    }

    sealed class StreamingClient(string[] chunks) : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new(nameof(StreamingClient));
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var c in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, c);
                await Task.Yield();
            }
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    sealed class RawClient(ChatResponse response) : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new(nameof(RawClient));
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromResult(response);
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}


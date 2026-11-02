using SignalGrid.Ampelum;
using SignalGrid.Ampelum.DependencyInjection;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Caching;

public sealed class AmpelumBuilderResponseCacheTests
{
    [Signature("")] public sealed record S : ISignature
    {
        [Input]  public required string Q { get; init; }
        [Output] public string A { get; init; } = "";
    }

    static IDistributedCache MemCache() =>
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    /// <summary>Creates a <see cref="ServiceCollection"/> pre-loaded with the services that
    /// <c>AddAmpelum</c> depends on but does not self-register.</summary>
    static ServiceCollection BaseServices()
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        return sc;
    }

    // â”€â”€ Verbatim plan test â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task WithResponseCache_PropagatesCacheHitToTraceMetadata()
    {
        var sc = BaseServices();
        var inner = new FakeChatClient()
            .EnqueueOutputs(new S { Q = "?", A = "Paris" })
            .EnqueueOutputs(new S { Q = "?", A = "Paris" });
        var cache = MemCache();
        sc.AddSingleton<IChatClient>(inner);
        var sink = new InMemoryTraceSink();
        sc.AddSingleton<ITraceSink>(sink);
        var builder = sc.AddAmpelum();
        builder.WithResponseCache(cache);
        var sp = sc.BuildServiceProvider();
        var predict = sp.GetRequiredService<Predict<S>>();
        var ct = TestContext.Current.CancellationToken;
        var one = await predict.ForwardAsync(new S { Q = "?" }, ct: ct);
        var two = await predict.ForwardAsync(new S { Q = "?" }, ct: ct);
        two.Trace.Metadata.ShouldContainKey(TraceKeys.CacheHit);
        two.Trace.Metadata[TraceKeys.CacheHit].ShouldBe(true);
        inner.RecordedCalls.Count.ShouldBe(1);
    }

    // â”€â”€ Null-arg checks â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public void WithResponseCache_NullBuilder_Throws()
    {
        var cache = MemCache();
        Should.Throw<ArgumentNullException>(() =>
            AmpelumBuilderCachingExtensions.WithResponseCache(null!, cache));
    }

    [Fact]
    public void WithResponseCache_NullCache_Throws()
    {
        var sc = BaseServices();
        sc.AddSingleton<IChatClient>(new FakeChatClient());
        var builder = sc.AddAmpelum();
        Should.Throw<ArgumentNullException>(() => builder.WithResponseCache(null!));
    }

    // â”€â”€ Missing IChatClient registration â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public void WithResponseCache_NoPriorIChatClient_ThrowsInvalidOperation()
    {
        // AddAmpelum without any IChatClient registration
        var sc = BaseServices();
        var builder = sc.AddAmpelum();
        Should.Throw<InvalidOperationException>(() => builder.WithResponseCache(MemCache()));
    }

    // â”€â”€ ImplementationInstance branch â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task WithResponseCache_ImplementationInstance_WrapsCorrectly()
    {
        // AddSingleton<IChatClient>(instance) â†’ prior.ImplementationInstance is set (non-null)
        var sc = BaseServices();
        var inner = new FakeChatClient()
            .EnqueueOutputs(new S { Q = "?", A = "Rome" })
            .EnqueueOutputs(new S { Q = "?", A = "Rome" });
        var sink = new InMemoryTraceSink();
        sc.AddSingleton<IChatClient>(inner);  // ImplementationInstance path
        sc.AddSingleton<ITraceSink>(sink);
        var builder = sc.AddAmpelum();
        builder.WithResponseCache(MemCache());
        var sp = sc.BuildServiceProvider();
        var predict = sp.GetRequiredService<Predict<S>>();
        var ct = TestContext.Current.CancellationToken;

        var one = await predict.ForwardAsync(new S { Q = "?" }, ct: ct);
        var two = await predict.ForwardAsync(new S { Q = "?" }, ct: ct);

        two.Trace.Metadata.ShouldContainKey(TraceKeys.CacheHit);
        two.Trace.Metadata[TraceKeys.CacheHit].ShouldBe(true);
        inner.RecordedCalls.Count.ShouldBe(1);
    }

    // â”€â”€ ImplementationFactory branch â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task WithResponseCache_ImplementationFactory_WrapsCorrectly()
    {
        // AddSingleton<IChatClient>(factory) â†’ prior.ImplementationFactory is set
        var sc = BaseServices();
        var inner = new FakeChatClient()
            .EnqueueOutputs(new S { Q = "?", A = "Berlin" })
            .EnqueueOutputs(new S { Q = "?", A = "Berlin" });
        var sink = new InMemoryTraceSink();
        sc.AddSingleton<IChatClient>(_ => inner);  // ImplementationFactory path
        sc.AddSingleton<ITraceSink>(sink);
        var builder = sc.AddAmpelum();
        builder.WithResponseCache(MemCache());
        var sp = sc.BuildServiceProvider();
        var predict = sp.GetRequiredService<Predict<S>>();
        var ct = TestContext.Current.CancellationToken;

        var one = await predict.ForwardAsync(new S { Q = "?" }, ct: ct);
        var two = await predict.ForwardAsync(new S { Q = "?" }, ct: ct);

        two.Trace.Metadata.ShouldContainKey(TraceKeys.CacheHit);
        two.Trace.Metadata[TraceKeys.CacheHit].ShouldBe(true);
        inner.RecordedCalls.Count.ShouldBe(1);
    }

    // â”€â”€ ImplementationType branch â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>A minimal IChatClient that always returns a fixed, parseable response.</summary>
    sealed class StubChatClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new(nameof(StubChatClient));
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## A ## ]]\nMadrid")));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task WithResponseCache_ImplementationType_WrapsCorrectly()
    {
        // AddSingleton<IChatClient, StubChatClient>() â†’ prior.ImplementationType is set.
        // WithResponseCache must use ActivatorUtilities.CreateInstance, NOT GetRequiredService,
        // because StubChatClient is NOT separately registered as its own service type.
        var sc = BaseServices();
        sc.AddSingleton<IChatClient, StubChatClient>();  // ImplementationType path
        var sink = new InMemoryTraceSink();
        sc.AddSingleton<ITraceSink>(sink);
        var builder = sc.AddAmpelum();
        var cache = MemCache();
        builder.WithResponseCache(cache);
        var sp = sc.BuildServiceProvider();
        var predict = sp.GetRequiredService<Predict<S>>();
        var ct = TestContext.Current.CancellationToken;

        // First call goes to StubChatClient (cache miss)
        var one = await predict.ForwardAsync(new S { Q = "?" }, ct: ct);
        one.Value.A.ShouldBe("Madrid");
        one.Trace.Metadata.ContainsKey(TraceKeys.CacheHit).ShouldBeFalse();

        // Second call is a cache hit
        var two = await predict.ForwardAsync(new S { Q = "?" }, ct: ct);
        two.Trace.Metadata.ShouldContainKey(TraceKeys.CacheHit);
        two.Trace.Metadata[TraceKeys.CacheHit].ShouldBe(true);

        // Verify StubChatClient is NOT directly resolvable (not registered as its own service type)
        sp.GetService<StubChatClient>().ShouldBeNull();
    }

    // â”€â”€ Non-cache-hit call emits no CacheHit key â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task NoCacheHit_FirstCall_MetadataDoesNotContainCacheHitKey()
    {
        // The first forward call against the caching wrapper is always a miss:
        // AdditionalProperties from FakeChatClient is null â†’ CacheHit key must NOT be stamped.
        var sc = BaseServices();
        var inner = new FakeChatClient()
            .EnqueueOutputs(new S { Q = "?", A = "Paris" });
        var sink = new InMemoryTraceSink();
        sc.AddSingleton<IChatClient>(inner);
        sc.AddSingleton<ITraceSink>(sink);
        var builder = sc.AddAmpelum();
        builder.WithResponseCache(MemCache());
        var sp = sc.BuildServiceProvider();
        var predict = sp.GetRequiredService<Predict<S>>();

        var one = await predict.ForwardAsync(new S { Q = "?" }, ct: TestContext.Current.CancellationToken);

        one.Trace.Metadata.ContainsKey(TraceKeys.CacheHit).ShouldBeFalse();
    }

    // â”€â”€ AdditionalProperties non-null but key absent â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task AdditionalProperties_NonNull_WrongKey_MetadataDoesNotContainCacheHitKey()
    {
        // Exercises the short-circuit: AdditionalProperties is not null,
        // but "SignalGrid.Ampelum.cache_hit" key is absent â†’ CacheHit must NOT be stamped.
        var responseWithOtherProps = new ChatResponse(
            new ChatMessage(ChatRole.Assistant, "[[ ## A ## ]]\nLisbon"))
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["something_else"] = 1 }
        };

        var sc = BaseServices();
        var sink = new InMemoryTraceSink();
        sc.AddSingleton<IChatClient>(_ => new RawResponseClient(responseWithOtherProps));
        sc.AddSingleton<ITraceSink>(sink);
        sc.AddAmpelum();
        var sp = sc.BuildServiceProvider();
        var predict = sp.GetRequiredService<Predict<S>>();

        var result = await predict.ForwardAsync(new S { Q = "?" }, ct: TestContext.Current.CancellationToken);

        result.Trace.Metadata.ContainsKey(TraceKeys.CacheHit).ShouldBeFalse();
    }

    // â”€â”€ AdditionalProperties has key but value is not `true` â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task AdditionalProperties_CacheHitKeyPresentButValueNotTrue_MetadataNotStamped()
    {
        // Exercises the short-circuit: key present but value is NOT `true` (e.g. false).
        var responseWithFalseHit = new ChatResponse(
            new ChatMessage(ChatRole.Assistant, "[[ ## A ## ]]\nVienna"))
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["SignalGrid.Ampelum.cache_hit"] = false }
        };

        var sc = BaseServices();
        var sink = new InMemoryTraceSink();
        sc.AddSingleton<IChatClient>(_ => new RawResponseClient(responseWithFalseHit));
        sc.AddSingleton<ITraceSink>(sink);
        sc.AddAmpelum();
        var sp = sc.BuildServiceProvider();
        var predict = sp.GetRequiredService<Predict<S>>();

        var result = await predict.ForwardAsync(new S { Q = "?" }, ct: TestContext.Current.CancellationToken);

        result.Trace.Metadata.ContainsKey(TraceKeys.CacheHit).ShouldBeFalse();
    }

    // â”€â”€ Returns builder for chaining â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public void WithResponseCache_ReturnsSameBuilder()
    {
        var sc = BaseServices();
        sc.AddSingleton<IChatClient>(new FakeChatClient());
        var builder = sc.AddAmpelum();
        var returned = builder.WithResponseCache(MemCache());
        returned.ShouldBeSameAs(builder);
    }

    // â”€â”€ I2 â€” preserves prior IChatClient ServiceLifetime â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public void WithResponseCache_PreservesLifetime_WhenPriorIsScoped()
    {
        var sc = BaseServices();
        sc.AddScoped<IChatClient>(_ => new FakeChatClient());
        var builder = sc.AddAmpelum();
        builder.WithResponseCache(MemCache());
        var descriptor = sc.LastOrDefault(d => d.ServiceType == typeof(IChatClient));
        descriptor.ShouldNotBeNull();
        descriptor!.Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }

    [Fact]
    public void WithResponseCache_PreservesLifetime_WhenPriorIsSingleton()
    {
        var sc = BaseServices();
        sc.AddSingleton<IChatClient>(new FakeChatClient());
        var builder = sc.AddAmpelum();
        builder.WithResponseCache(MemCache());
        var descriptor = sc.LastOrDefault(d => d.ServiceType == typeof(IChatClient));
        descriptor.ShouldNotBeNull();
        descriptor!.Lifetime.ShouldBe(ServiceLifetime.Singleton);
    }

    [Fact]
    public void WithResponseCache_PreservesLifetime_WhenPriorIsTransient()
    {
        var sc = BaseServices();
        sc.AddTransient<IChatClient>(_ => new FakeChatClient());
        var builder = sc.AddAmpelum();
        builder.WithResponseCache(MemCache());
        var descriptor = sc.LastOrDefault(d => d.ServiceType == typeof(IChatClient));
        descriptor.ShouldNotBeNull();
        descriptor!.Lifetime.ShouldBe(ServiceLifetime.Transient);
    }

    // â”€â”€ Helper client â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>Returns a pre-built <see cref="ChatResponse"/> verbatim on every call.</summary>
    sealed class RawResponseClient(ChatResponse response) : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new(nameof(RawResponseClient));
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromResult(response);
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

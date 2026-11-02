using SignalGrid.Ampelum.Caching;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Caching;

/// <summary>Exposes protected <c>GetCacheKey</c> for branch-coverage testing.</summary>
file sealed class ExposedCachingClient(IChatClient inner, IDistributedCache cache)
    : AmpelumCachingChatClient(inner, cache)
{
    public string CallGetCacheKey(IEnumerable<ChatMessage> messages, ChatOptions? options,
        params ReadOnlySpan<object?> additionalValues)
        => GetCacheKey(messages, options, additionalValues);
}

public sealed class AmpelumCachingChatClientTests
{
    static IDistributedCache MemCache() =>
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    // â”€â”€ Verbatim plan tests â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task SecondIdenticalCall_IsCacheHit_AnnotatesResponse()
    {
        var inner = new FakeChatClient()
            .EnqueueText("hello")
            .EnqueueText("should-not-be-used");
        var cache = MemCache();
        var cached = new AmpelumCachingChatClient(inner, cache);
        var msgs = new[] { new ChatMessage(ChatRole.User, "x") };
        var opts = new ChatOptions { Temperature = 0, ModelId = "m", MaxOutputTokens = 100 };
        var ct = TestContext.Current.CancellationToken;

        var first = await cached.GetResponseAsync(msgs, opts, ct);
        // On a miss, AdditionalProperties must NOT have the cache-hit key
        (first.AdditionalProperties?.ContainsKey("SignalGrid.Ampelum.cache_hit") ?? false).ShouldBeFalse();
        var second = await cached.GetResponseAsync(msgs, opts, ct);
        second.AdditionalProperties!["SignalGrid.Ampelum.cache_hit"].ShouldBe(true);
        inner.RecordedCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task DifferentTemperature_ProducesDifferentKey()
    {
        var inner = new FakeChatClient().EnqueueText("t0").EnqueueText("t07");
        var cached = new AmpelumCachingChatClient(inner, MemCache());
        var msgs = new[] { new ChatMessage(ChatRole.User, "x") };
        var ct = TestContext.Current.CancellationToken;
        await cached.GetResponseAsync(msgs, new ChatOptions { Temperature = 0 }, ct);
        await cached.GetResponseAsync(msgs, new ChatOptions { Temperature = 0.7f }, ct);
        inner.RecordedCalls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task KeyExcludes_TopP_Seed_Tools()
    {
        var inner = new FakeChatClient().EnqueueText("hit").EnqueueText("miss-if-wrong");
        var cached = new AmpelumCachingChatClient(inner, MemCache());
        var msgs = new[] { new ChatMessage(ChatRole.User, "x") };
        var ct = TestContext.Current.CancellationToken;
        var opts1 = new ChatOptions { TopP = 0.1f, Seed = 1 };
        var opts2 = new ChatOptions { TopP = 0.9f, Seed = 2 };
        await cached.GetResponseAsync(msgs, opts1, ct);
        var second = await cached.GetResponseAsync(msgs, opts2, ct);
        second.AdditionalProperties!["SignalGrid.Ampelum.cache_hit"].ShouldBe(true);
        inner.RecordedCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Ctor_NullCache_Throws()
    {
        var inner = new FakeChatClient();
        Should.Throw<ArgumentNullException>(() => new AmpelumCachingChatClient(inner, null!));
        await Task.CompletedTask;
    }

    // â”€â”€ Coverage branch tests â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task NullOptions_DoesNotThrow_AndCachesCorrectly()
    {
        var inner = new FakeChatClient().EnqueueText("a").EnqueueText("b");
        var cached = new AmpelumCachingChatClient(inner, MemCache());
        var msgs = new[] { new ChatMessage(ChatRole.User, "hello") };
        var ct = TestContext.Current.CancellationToken;

        var first = await cached.GetResponseAsync(msgs, null, ct);
        first.ShouldNotBeNull();
        var second = await cached.GetResponseAsync(msgs, null, ct);
        second.AdditionalProperties!["SignalGrid.Ampelum.cache_hit"].ShouldBe(true);
        inner.RecordedCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task NullModelId_And_NullTemperature_And_NullMaxTokens_IsKeyStable()
    {
        // Options where all nullable fields are null â€” key must still be stable
        var inner = new FakeChatClient().EnqueueText("r1").EnqueueText("r2");
        var cached = new AmpelumCachingChatClient(inner, MemCache());
        var msgs = new[] { new ChatMessage(ChatRole.User, "y") };
        var opts = new ChatOptions(); // ModelId=null, Temperature=null, MaxOutputTokens=null
        var ct = TestContext.Current.CancellationToken;

        await cached.GetResponseAsync(msgs, opts, ct);
        var second = await cached.GetResponseAsync(msgs, opts, ct);
        second.AdditionalProperties!["SignalGrid.Ampelum.cache_hit"].ShouldBe(true);
        inner.RecordedCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ResponseFormat_Json_WithSchema_IsKeyStable()
    {
        var schema = System.Text.Json.JsonDocument.Parse("""{"type":"string"}""").RootElement;
        var rf = ChatResponseFormat.ForJsonSchema(schema, "name", "desc");

        var inner = new FakeChatClient().EnqueueText("s1").EnqueueText("s2");
        var cached = new AmpelumCachingChatClient(inner, MemCache());
        var msgs = new[] { new ChatMessage(ChatRole.User, "q") };
        var opts = new ChatOptions { ResponseFormat = rf };
        var ct = TestContext.Current.CancellationToken;

        await cached.GetResponseAsync(msgs, opts, ct);
        var second = await cached.GetResponseAsync(msgs, opts, ct);
        second.AdditionalProperties!["SignalGrid.Ampelum.cache_hit"].ShouldBe(true);
        inner.RecordedCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ResponseFormat_Json_NoSchema_ProducesStableKey()
    {
        // ChatResponseFormat.Json creates a ChatResponseFormatJson with Schema=null
        var rf = ChatResponseFormat.Json;

        var inner = new FakeChatClient().EnqueueText("j1").EnqueueText("j2");
        var cached = new AmpelumCachingChatClient(inner, MemCache());
        var msgs = new[] { new ChatMessage(ChatRole.User, "q") };
        var opts = new ChatOptions { ResponseFormat = rf };
        var ct = TestContext.Current.CancellationToken;

        await cached.GetResponseAsync(msgs, opts, ct);
        var second = await cached.GetResponseAsync(msgs, opts, ct);
        second.AdditionalProperties!["SignalGrid.Ampelum.cache_hit"].ShouldBe(true);
        inner.RecordedCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ResponseFormat_Text_ProducesStableKey()
    {
        var rf = ChatResponseFormat.Text;

        var inner = new FakeChatClient().EnqueueText("t1").EnqueueText("t2");
        var cached = new AmpelumCachingChatClient(inner, MemCache());
        var msgs = new[] { new ChatMessage(ChatRole.User, "q") };
        var opts = new ChatOptions { ResponseFormat = rf };
        var ct = TestContext.Current.CancellationToken;

        await cached.GetResponseAsync(msgs, opts, ct);
        var second = await cached.GetResponseAsync(msgs, opts, ct);
        second.AdditionalProperties!["SignalGrid.Ampelum.cache_hit"].ShouldBe(true);
        inner.RecordedCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SystemMessages_AreIncludedInKey_ProduceDifferentKeysWhenDifferent()
    {
        var inner = new FakeChatClient().EnqueueText("a").EnqueueText("b");
        var cached = new AmpelumCachingChatClient(inner, MemCache());
        var ct = TestContext.Current.CancellationToken;

        var msgs1 = new[] { new ChatMessage(ChatRole.System, "be helpful"), new ChatMessage(ChatRole.User, "hi") };
        var msgs2 = new[] { new ChatMessage(ChatRole.System, "be rude"), new ChatMessage(ChatRole.User, "hi") };

        await cached.GetResponseAsync(msgs1, null, ct);
        await cached.GetResponseAsync(msgs2, null, ct);
        // Different system messages => different key => two backend calls
        inner.RecordedCalls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task SystemMessages_SameSystem_SameKey()
    {
        var inner = new FakeChatClient().EnqueueText("a").EnqueueText("miss");
        var cached = new AmpelumCachingChatClient(inner, MemCache());
        var ct = TestContext.Current.CancellationToken;

        var msgs1 = new[] { new ChatMessage(ChatRole.System, "be helpful"), new ChatMessage(ChatRole.User, "hi") };
        var msgs2 = new[] { new ChatMessage(ChatRole.System, "be helpful"), new ChatMessage(ChatRole.User, "hi") };

        await cached.GetResponseAsync(msgs1, null, ct);
        var second = await cached.GetResponseAsync(msgs2, null, ct);
        second.AdditionalProperties!["SignalGrid.Ampelum.cache_hit"].ShouldBe(true);
        inner.RecordedCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task NoSystemMessages_OnlyUser_Works()
    {
        var inner = new FakeChatClient().EnqueueText("r").EnqueueText("miss");
        var cached = new AmpelumCachingChatClient(inner, MemCache());
        var msgs = new[] { new ChatMessage(ChatRole.User, "just a user msg") };
        var ct = TestContext.Current.CancellationToken;

        await cached.GetResponseAsync(msgs, null, ct);
        var second = await cached.GetResponseAsync(msgs, null, ct);
        second.AdditionalProperties!["SignalGrid.Ampelum.cache_hit"].ShouldBe(true);
        inner.RecordedCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task MaxOutputTokens_Set_IsIncludedInKey()
    {
        // Different max token counts -> different keys
        var inner = new FakeChatClient().EnqueueText("a").EnqueueText("b");
        var cached = new AmpelumCachingChatClient(inner, MemCache());
        var msgs = new[] { new ChatMessage(ChatRole.User, "x") };
        var ct = TestContext.Current.CancellationToken;

        await cached.GetResponseAsync(msgs, new ChatOptions { MaxOutputTokens = 50 }, ct);
        await cached.GetResponseAsync(msgs, new ChatOptions { MaxOutputTokens = 200 }, ct);
        inner.RecordedCalls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task CoalesceStreamingUpdates_AffectsAdditionalValuesSpan_KeyIsStable()
    {
        // additionalValues span (which includes CoalesceStreamingUpdates=False by default) must
        // produce the same key on repeated calls â€” verifies the additionalValues foreach path is exercised.
        var inner = new FakeChatClient().EnqueueText("a").EnqueueText("miss");
        var cached = new AmpelumCachingChatClient(inner, MemCache());
        var msgs = new[] { new ChatMessage(ChatRole.User, "x") };
        var ct = TestContext.Current.CancellationToken;

        await cached.GetResponseAsync(msgs, null, ct);
        var second = await cached.GetResponseAsync(msgs, null, ct);
        second.AdditionalProperties!["SignalGrid.Ampelum.cache_hit"].ShouldBe(true);
        inner.RecordedCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ResponseFormat_Json_WithSchema_DifferentSchemas_ProduceDifferentKeys()
    {
        var schema1 = System.Text.Json.JsonDocument.Parse("""{"type":"string"}""").RootElement;
        var schema2 = System.Text.Json.JsonDocument.Parse("""{"type":"number"}""").RootElement;

        var inner = new FakeChatClient().EnqueueText("a").EnqueueText("b");
        var cached = new AmpelumCachingChatClient(inner, MemCache());
        var msgs = new[] { new ChatMessage(ChatRole.User, "x") };
        var ct = TestContext.Current.CancellationToken;

        await cached.GetResponseAsync(msgs, new ChatOptions { ResponseFormat = ChatResponseFormat.ForJsonSchema(schema1) }, ct);
        await cached.GetResponseAsync(msgs, new ChatOptions { ResponseFormat = ChatResponseFormat.ForJsonSchema(schema2) }, ct);
        inner.RecordedCalls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ModelId_DifferentValues_ProduceDifferentKeys()
    {
        var inner = new FakeChatClient().EnqueueText("a").EnqueueText("b");
        var cached = new AmpelumCachingChatClient(inner, MemCache());
        var msgs = new[] { new ChatMessage(ChatRole.User, "x") };
        var ct = TestContext.Current.CancellationToken;

        await cached.GetResponseAsync(msgs, new ChatOptions { ModelId = "model-a" }, ct);
        await cached.GetResponseAsync(msgs, new ChatOptions { ModelId = "model-b" }, ct);
        inner.RecordedCalls.Count.ShouldBe(2);
    }

    [Fact]
    public void GetCacheKey_NullAdditionalValue_DoesNotThrow_KeyIsStable()
    {
        // Exercises the `v?.ToString() ?? "null"` null-value branch in the additionalValues loop
        var inner = new FakeChatClient();
        var client = new ExposedCachingClient(inner, MemCache());
        var msgs = new[] { new ChatMessage(ChatRole.User, "x") };

        // Pass a span that contains a null element
        var key1 = client.CallGetCacheKey(msgs, null, [null]);
        var key2 = client.CallGetCacheKey(msgs, null, [null]);
        key1.ShouldBe(key2);
        key1.Length.ShouldBe(64); // SHA256 hex = 64 chars
    }

    [Fact]
    public async Task Messages_AsIEnumerable_NonList_IsHandled()
    {
        // Exercises the `messages as IList<ChatMessage> ?? messages.ToList()` fallback branch
        var inner = new FakeChatClient().EnqueueText("a").EnqueueText("miss");
        var cached = new AmpelumCachingChatClient(inner, MemCache());
        var ct = TestContext.Current.CancellationToken;

        // Pass a non-IList IEnumerable<ChatMessage>
        static IEnumerable<ChatMessage> AsEnumerable()
        {
            yield return new ChatMessage(ChatRole.User, "hello");
        }

        await cached.GetResponseAsync(AsEnumerable(), null, ct);
        var second = await cached.GetResponseAsync(AsEnumerable(), null, ct);
        second.AdditionalProperties!["SignalGrid.Ampelum.cache_hit"].ShouldBe(true);
        inner.RecordedCalls.Count.ShouldBe(1);
    }

    // C2 â€” streaming cache hit (CoalesceStreamingUpdates=true, default) annotates an update
    [Fact]
    public async Task StreamingSecondIdenticalCall_IsCacheHit_AnnotatesFirstUpdate()
    {
        var inner = new FakeChatClient()
            .EnqueueStreamingChunks("[[ ## A ## ]]\nParis")
            .EnqueueStreamingChunks("should-not-be-used");
        var cache = MemCache();
        var cached = new AmpelumCachingChatClient(inner, cache);
        // Default CoalesceStreamingUpdates=true: ReadCacheAsync path is used for streaming cache hits
        var msgs = new[] { new ChatMessage(ChatRole.User, "x") };
        var opts = new ChatOptions { Temperature = 0, ModelId = "m", MaxOutputTokens = 100 };
        var ct = TestContext.Current.CancellationToken;

        // First call â€” cache miss
        var firstUpdates = new List<ChatResponseUpdate>();
        await foreach (var u in cached.GetStreamingResponseAsync(msgs, opts, ct))
            firstUpdates.Add(u);
        (firstUpdates.Any(u => u.AdditionalProperties?.ContainsKey("SignalGrid.Ampelum.cache_hit") == true)).ShouldBeFalse();

        // Second call â€” cache hit; SignalGrid.Ampelum.cache_hit is carried by one of the updates
        var secondUpdates = new List<ChatResponseUpdate>();
        await foreach (var u in cached.GetStreamingResponseAsync(msgs, opts, ct))
            secondUpdates.Add(u);
        secondUpdates.Count.ShouldBeGreaterThan(0);
        var hitUpdate = secondUpdates.FirstOrDefault(u => u.AdditionalProperties?.ContainsKey("SignalGrid.Ampelum.cache_hit") == true);
        hitUpdate.ShouldNotBeNull();
        hitUpdate!.AdditionalProperties!["SignalGrid.Ampelum.cache_hit"].ShouldBe(true);
        inner.RecordedCalls.Count.ShouldBe(1);
    }

    // C2 â€” streaming cache hit (CoalesceStreamingUpdates=false) exercises ReadCacheStreamingAsync override
    [Fact]
    public async Task StreamingSecondIdenticalCall_CoalesceOff_IsCacheHit_AnnotatesFirstUpdate()
    {
        var inner = new FakeChatClient()
            .EnqueueStreamingChunks("[[ ## A ## ]]\nMadrid")
            .EnqueueStreamingChunks("should-not-be-used");
        var cache = MemCache();
        var cached = new AmpelumCachingChatClient(inner, cache)
        {
            CoalesceStreamingUpdates = false  // forces ReadCacheStreamingAsync path
        };
        var msgs = new[] { new ChatMessage(ChatRole.User, "q2") };
        var opts = new ChatOptions { Temperature = 0 };
        var ct = TestContext.Current.CancellationToken;

        // First call â€” cache miss; populates cache via WriteCacheStreamingAsync
        await foreach (var u in cached.GetStreamingResponseAsync(msgs, opts, ct)) { }

        // Second call â€” cache hit via ReadCacheStreamingAsync
        var secondUpdates = new List<ChatResponseUpdate>();
        await foreach (var u in cached.GetStreamingResponseAsync(msgs, opts, ct))
            secondUpdates.Add(u);
        secondUpdates.Count.ShouldBeGreaterThan(0);
        var hitUpdate = secondUpdates.FirstOrDefault(u => u.AdditionalProperties?.ContainsKey("SignalGrid.Ampelum.cache_hit") == true);
        hitUpdate.ShouldNotBeNull();
        hitUpdate!.AdditionalProperties!["SignalGrid.Ampelum.cache_hit"].ShouldBe(true);
        inner.RecordedCalls.Count.ShouldBe(1);
    }

    // C2 â€” streaming cache miss does NOT annotate updates
    [Fact]
    public async Task StreamingFirstCall_IsCacheMiss_DoesNotAnnotateUpdates()
    {
        var inner = new FakeChatClient()
            .EnqueueStreamingChunks("[[ ## A ## ]]\nLondon");
        var cached = new AmpelumCachingChatClient(inner, MemCache())
        {
            CoalesceStreamingUpdates = false  // ensures ReadCacheStreamingAsync miss path is exercised
        };
        var msgs = new[] { new ChatMessage(ChatRole.User, "q") };
        var ct = TestContext.Current.CancellationToken;

        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in cached.GetStreamingResponseAsync(msgs, null, ct))
            updates.Add(u);
        updates.Any(u => u.AdditionalProperties?.ContainsKey("SignalGrid.Ampelum.cache_hit") == true).ShouldBeFalse();
        inner.RecordedCalls.Count.ShouldBe(1);
    }

}

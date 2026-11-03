using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Caching.Semantic;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace Ampelum.Tests.Caching.Semantic;

/// <summary>A streamed turn never serves from the semantic cache, so it must not fill it or pay for an embedding either.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class SemanticCacheStreamingTests
{
    sealed class CountingEmbeddings : IEmbeddingGenerator<string, Embedding<float>>
    {
        public int Calls;
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(_ => new Embedding<float>(new float[] { 1, 0 }))));
        }
    }

    [Fact]
    public async Task A_streamed_turn_neither_stores_nor_embeds_and_the_model_is_called_every_time()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = new InMemorySemanticCache();
        var embeddings = new CountingEmbeddings();
        var chat = new FakeChatClient().EnqueueStreamingChunks("one").EnqueueStreamingChunks("two");
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
            .WithHook(new SemanticCacheHook(cache, embeddings));
        var session = new StreamingChatSession(ctx, "Sys");

        await foreach (var _ in session.SendAsync("same message", ct)) { }
        await foreach (var _ in session.SendAsync("same message", ct)) { }

        cache.Count.ShouldBe(0);
        embeddings.Calls.ShouldBe(0);
        chat.RecordedCalls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_streamed_context_is_not_served_from_an_entry_a_non_streamed_call_stored()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = new InMemorySemanticCache();
        var hook = new SemanticCacheHook(cache);
        HookContext Make() => new()
        {
            ModuleName = "Predict",
            Inputs = new Dictionary<string, object?> { ["Q"] = "q" },
        };
        var stored = Make();
        stored.Outputs = new Dictionary<string, object?> { ["A"] = "a" };
        await hook.OnAfterAsync(stored, ct);
        cache.Count.ShouldBe(1);

        var streamed = Make();
        streamed.Set(HookMetadataKeys.Streamed, true);
        await hook.OnBeforeAsync(streamed, ct);

        streamed.Outputs.ShouldBeNull();
        var plain = Make();
        await hook.OnBeforeAsync(plain, ct);
        plain.Outputs.ShouldNotBeNull();
    }
}

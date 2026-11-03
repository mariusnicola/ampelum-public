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

/// <summary>The embedding service is an accelerator: when it is down the model call still succeeds and exact matches still hit.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class SemanticCacheEmbeddingFailureTests
{
    sealed class FlakyEmbeddings : IEmbeddingGenerator<string, Embedding<float>>
    {
        public bool Down;
        public int Calls;
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Down) throw new HttpRequestException("embedding service down");
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(_ => new Embedding<float>(new float[] { 1, 0 }))));
        }
    }

    static HookContext Context(string question) => new()
    {
        ModuleName = "Predict",
        Inputs = new Dictionary<string, object?> { ["Q"] = question },
    };

    [Fact]
    public async Task A_failing_embedding_call_after_the_model_call_stores_the_exact_entry_and_does_not_throw()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = new InMemorySemanticCache();
        var embeddings = new FlakyEmbeddings { Down = true };
        var hook = new SemanticCacheHook(cache, embeddings);
        var miss = Context("q");
        miss.Outputs = new Dictionary<string, object?> { ["A"] = "a" };

        await hook.OnAfterAsync(miss, ct);

        embeddings.Calls.ShouldBe(1);
        cache.Count.ShouldBe(1);
        var again = Context("q");
        await hook.OnBeforeAsync(again, ct);
        again.Outputs!["A"].ShouldBe("a");
    }

    [Fact]
    public async Task A_failing_embedding_call_on_lookup_falls_back_to_a_miss_and_exact_match_still_hits()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = new InMemorySemanticCache();
        var embeddings = new FlakyEmbeddings();
        var hook = new SemanticCacheHook(cache, embeddings);
        var first = Context("original question");
        first.Outputs = new Dictionary<string, object?> { ["A"] = "a" };
        await hook.OnAfterAsync(first, ct);
        embeddings.Down = true;

        var reworded = Context("reworded question");
        await hook.OnBeforeAsync(reworded, ct);
        var exact = Context("original question");
        await hook.OnBeforeAsync(exact, ct);

        reworded.Outputs.ShouldBeNull();
        exact.Outputs!["A"].ShouldBe("a");
    }

    [Signature("Answer the question.")]
    public sealed record Ask : ISignature
    {
        [Input] public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    [Fact]
    public async Task Predict_succeeds_when_the_embedding_service_is_down()
    {
        var ct = TestContext.Current.CancellationToken;
        var chat = new FakeChatClient().EnqueueText("[[ ## Answer ## ]]\nyes\n[[ ## completed ## ]]");
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
            .WithHook(new SemanticCacheHook(new InMemorySemanticCache(), new FlakyEmbeddings { Down = true }));

        var result = await new Predict<Ask>(ctx).ForwardAsync(new Ask { Question = "q" }, ct: ct);

        result.Value.Answer.ShouldBe("yes");
    }

    [Fact]
    public async Task A_cancelled_embedding_call_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        var cache = new InMemorySemanticCache();
        var hook = new SemanticCacheHook(cache, new CancellingEmbeddings(cts));
        var miss = Context("q");
        miss.Outputs = new Dictionary<string, object?> { ["A"] = "a" };

        await Should.ThrowAsync<OperationCanceledException>(() => hook.OnAfterAsync(miss, cts.Token).AsTask());
    }

    sealed class CancellingEmbeddings(CancellationTokenSource cts) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            cts.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>());
        }
    }
}

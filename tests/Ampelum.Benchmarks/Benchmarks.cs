using BenchmarkDotNet.Attributes;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Internal;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

// ──────────────────────────────────────────────────────────────────────────────
// Shared fixture types
// ──────────────────────────────────────────────────────────────────────────────

[Signature("Answer the question.")]
public sealed record BenchQa : ISignature
{
    [Input]  public required string Question { get; init; }
    [Output] public string Answer { get; init; } = "";
}

[Signature("Summarize text with sentiment.")]
public sealed record BenchMulti : ISignature
{
    [Input]  public required string Text { get; init; }
    [Input]  public required string Language { get; init; }
    [Output] public string Summary { get; init; } = "";
    [Output] public string Sentiment { get; init; } = "";
    [Output] public int WordCount { get; init; }
}

/// <summary>No-op chat client that returns a canned ChatResponse instantly.</summary>
sealed class NullChatClient(ChatResponse response) : IChatClient
{
    public ChatClientMetadata Metadata { get; } = new("null");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default) => Task.FromResult(response);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default) => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

// ──────────────────────────────────────────────────────────────────────────────
// 1. Predict.ForwardAsync end-to-end (ChatAdapter)
// ──────────────────────────────────────────────────────────────────────────────
[MemoryDiagnoser]
public class PredictForwardBenchmark
{
    Predict<BenchQa> _predict = null!;

    [GlobalSetup]
    public void Setup()
    {
        var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## Answer ## ]]\nParis"));
        var ctx = new AmpelumContext(
            new NullChatClient(resp),
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance);
        _predict = new Predict<BenchQa>(ctx);
    }

    [Benchmark]
    public async Task<Prediction<BenchQa>> ForwardAsync() =>
        await _predict.ForwardAsync(new BenchQa { Question = "Capital of France?" });
}

// ──────────────────────────────────────────────────────────────────────────────
// 2. SignatureMetadataCache.For<T>() — first call vs cached
// ──────────────────────────────────────────────────────────────────────────────
[MemoryDiagnoser]
public class SignatureMetadataCacheBenchmark
{
    // Use a dedicated type so we control first-call vs cached independently
    [Signature("Cache bench.")]
    sealed record CacheTarget : ISignature
    {
        [Input]  public required string A { get; init; }
        [Output] public string B { get; init; } = "";
    }

    [GlobalSetup]
    public void Setup()
    {
        // Prime the cache so the [Benchmark] method is always a cache-hit
        SignatureMetadataCache.For<CacheTarget>();
    }

    [Benchmark]
    public SignatureMetadata CachedLookup() => SignatureMetadataCache.For<CacheTarget>();
}

// ──────────────────────────────────────────────────────────────────────────────
// 3. ChatAdapter.Format + ChatAdapter.Parse
// ──────────────────────────────────────────────────────────────────────────────
[MemoryDiagnoser]
public class ChatAdapterBenchmark
{
    ChatAdapter _adapter = null!;
    SignatureMetadata _meta = null!;
    IReadOnlyDictionary<string, object?> _inputs = null!;
    IReadOnlyList<Example> _demos = [];
    ChatResponse _response = null!;

    [GlobalSetup]
    public void Setup()
    {
        _adapter = new ChatAdapter();
        _meta = SignatureMetadataCache.For<BenchMulti>();
        _inputs = new Dictionary<string, object?>
        {
            ["Text"]     = "The quick brown fox jumps over the lazy dog.",
            ["Language"] = "English"
        };
        _response = new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            "[[ ## Summary ## ]]\nA fox jumps.\n[[ ## Sentiment ## ]]\nneutral\n[[ ## WordCount ## ]]\n9"));
    }

    [Benchmark]
    public IReadOnlyList<ChatMessage> Format() =>
        _adapter.Format(_meta, _inputs, _demos);

    [Benchmark]
    public IReadOnlyDictionary<string, object?> Parse() =>
        _adapter.Parse(_meta, _response);
}

// ──────────────────────────────────────────────────────────────────────────────
// 4. JsonAdapter.Format + JsonAdapter.Parse + BuildResponseFormat
// ──────────────────────────────────────────────────────────────────────────────
[MemoryDiagnoser]
public class JsonAdapterBenchmark
{
    JsonAdapter _adapter = null!;
    SignatureMetadata _meta = null!;
    IReadOnlyDictionary<string, object?> _inputs = null!;
    IReadOnlyList<Example> _demos = [];
    ChatResponse _response = null!;

    [GlobalSetup]
    public void Setup()
    {
        _adapter = new JsonAdapter();
        _meta = SignatureMetadataCache.For<BenchMulti>();
        _inputs = new Dictionary<string, object?>
        {
            ["Text"]     = "The quick brown fox jumps over the lazy dog.",
            ["Language"] = "English"
        };
        _response = new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            """{"summary":"A fox jumps.","sentiment":"neutral","wordCount":9}"""));
    }

    [Benchmark]
    public IReadOnlyList<ChatMessage> Format() =>
        _adapter.Format(_meta, _inputs, _demos);

    [Benchmark]
    public IReadOnlyDictionary<string, object?> Parse() =>
        _adapter.Parse(_meta, _response);

    [Benchmark]
    public ChatResponseFormat BuildResponseFormat() =>
        _adapter.BuildResponseFormat(_meta);
}

// ──────────────────────────────────────────────────────────────────────────────
// 5. Predict.ForwardAsync end-to-end — 5-field record (exercises RecordCloner)
// ──────────────────────────────────────────────────────────────────────────────
[MemoryDiagnoser]
public class RecordClonerViaPredictBenchmark
{
    [Signature("Five-field clone bench.")]
    public sealed record FiveField : ISignature
    {
        [Input]  public required string F1 { get; init; }
        [Input]  public required string F2 { get; init; }
        [Output] public string F3 { get; init; } = "";
        [Output] public string F4 { get; init; } = "";
        [Output] public string F5 { get; init; } = "";
    }

    Predict<FiveField> _predict = null!;

    [GlobalSetup]
    public void Setup()
    {
        var resp = new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            "[[ ## F3 ## ]]\nx\n[[ ## F4 ## ]]\ny\n[[ ## F5 ## ]]\nz"));
        var ctx = new AmpelumContext(
            new NullChatClient(resp),
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance);
        _predict = new Predict<FiveField>(ctx);
        // Prime all internal caches
        _predict.ForwardAsync(new FiveField { F1 = "a", F2 = "b" }).GetAwaiter().GetResult();
    }

    [Benchmark]
    public async Task<Prediction<FiveField>> ForwardAsync() =>
        await _predict.ForwardAsync(new FiveField { F1 = "a", F2 = "b" });
}

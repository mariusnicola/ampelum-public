using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.Hooks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

[Signature("Answer.")]
public sealed record GateSig : ISignature
{
    [Input]  public required string Question { get; init; }
    [Output] public string Answer { get; init; } = "";
}

[MemoryDiagnoser]
[SimpleJob(launchCount: 3, warmupCount: 5, iterationCount: 10)]
[Config(typeof(InProcessConfig))]
public class PredictBaselineGate
{
    Predict<GateSig> _predict = null!;

    [GlobalSetup]
    public void Setup()
    {
        var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## Answer ## ]]\nParis"));
        var ctx = new AmpelumContext(
            new GateNullChat(resp),
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance);
        _predict = new Predict<GateSig>(ctx);
        _ = _predict.ForwardAsync(new GateSig { Question = "warm" }).GetAwaiter().GetResult();
    }

    [Benchmark]
    public async Task<Prediction<GateSig>> Forward() =>
        await _predict.ForwardAsync(new GateSig { Question = "Capital of France?" });
}

[MemoryDiagnoser]
[SimpleJob(launchCount: 3, warmupCount: 5, iterationCount: 10)]
[Config(typeof(InProcessConfig))]
public class PredictFullPipelineGate
{
    Predict<GateSig> _predict = null!;

    [GlobalSetup]
    public void Setup()
    {
        var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## Answer ## ]]\nParis"));
        var budget = SignalGrid.Ampelum.Budget.Budget.Create()
            .WithTokenLimit(1_000_000_000)
            .WithCallLimit(int.MaxValue / 2)
            .Build();
        var hooks = new IHook[]
        {
            new BudgetHook(budget),
            new AuditHook(new InMemoryAuditSink()),
        };
        var ctx = new AmpelumContext(
            new GateNullChat(resp),
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance) with
        { Hooks = hooks };
        _predict = new Predict<GateSig>(ctx);
        _ = _predict.ForwardAsync(new GateSig { Question = "warm" }).GetAwaiter().GetResult();
    }

    [Benchmark]
    public async Task<Prediction<GateSig>> Forward() =>
        await _predict.ForwardAsync(new GateSig { Question = "Capital of France?" });
}

[MemoryDiagnoser]
[SimpleJob(launchCount: 3, warmupCount: 5, iterationCount: 10)]
[Config(typeof(InProcessConfig))]
public class AuditEntryHashGate
{
    AuditEntry _entry = null!;

    [GlobalSetup]
    public void Setup() =>
        _entry = new AuditEntry
        {
            Sequence = 7,
            Timestamp = DateTimeOffset.UnixEpoch,
            ModuleName = "Predict",
            ModelId = "test-model",
            Inputs = new Dictionary<string, object?> { ["q"] = "ping" },
            Outputs = new Dictionary<string, object?> { ["a"] = "pong" },
            PromptTokens = 10,
            CompletionTokens = 5,
            PrevHash = new byte[32],
            EntryHash = new byte[32],
        };

    [Benchmark]
    public byte[] Compute() => AuditChainHasher.Compute(
        _entry.Sequence,
        _entry.Timestamp,
        _entry.ModuleName,
        _entry.ModelId,
        _entry.Inputs,
        _entry.Outputs,
        _entry.PromptTokens,
        _entry.CompletionTokens,
        _entry.PrevHash);
}

/// <summary>
/// v1.0-rc1.alpha.13: V3 chain hash gate — measures overhead of folding TraceId
/// into the SHA-256 chain versus V1 (no EventType, no TraceId).
/// </summary>
[MemoryDiagnoser]
[SimpleJob(launchCount: 3, warmupCount: 5, iterationCount: 10)]
[Config(typeof(InProcessConfig))]
public class AuditEntryHashV3Gate
{
    AuditEntry _entry = null!;

    [GlobalSetup]
    public void Setup() =>
        _entry = new AuditEntry
        {
            SchemaVersion = 3,
            EventType = "forward.completed",
            TraceId = "deadbeefdeadbeefdeadbeefdeadbeef",
            Sequence = 7,
            Timestamp = DateTimeOffset.UnixEpoch,
            ModuleName = "Predict",
            ModelId = "test-model",
            Inputs = new Dictionary<string, object?> { ["q"] = "ping" },
            Outputs = new Dictionary<string, object?> { ["a"] = "pong" },
            PromptTokens = 10,
            CompletionTokens = 5,
            PrevHash = new byte[32],
            EntryHash = new byte[32],
        };

    [Benchmark]
    public byte[] ComputeV3() => AuditChainHasher.ComputeV3(
        _entry.Sequence,
        _entry.Timestamp,
        _entry.ModuleName,
        _entry.EventType,
        _entry.ModelId,
        _entry.Inputs,
        _entry.Outputs,
        _entry.PromptTokens,
        _entry.CompletionTokens,
        _entry.TraceId,
        _entry.PrevHash);
}

[MemoryDiagnoser]
[SimpleJob(launchCount: 3, warmupCount: 5, iterationCount: 10)]
[Config(typeof(InProcessConfig))]
public class BudgetConsumeGate
{
    SignalGrid.Ampelum.Budget.IBudget _budget = null!;

    [GlobalSetup]
    public void Setup() =>
        _budget = SignalGrid.Ampelum.Budget.Budget.Create()
            .WithTokenLimit(long.MaxValue / 2)
            .WithCallLimit(int.MaxValue / 2)
            .Build();

    [Benchmark]
    public void Consume() => _budget.Consume(tokens: 1, costCents: 0m, calls: 1);
}

[MemoryDiagnoser]
[SimpleJob(launchCount: 3, warmupCount: 5, iterationCount: 10)]
[Config(typeof(InProcessConfig))]
public class ChatAdapterAllocGate
{
    ChatAdapter _adapter = null!;
    SignatureMetadata _meta = null!;
    ChatResponse _response = null!;

    [GlobalSetup]
    public void Setup()
    {
        _adapter = new ChatAdapter();
        _meta = SignatureMetadataCache.For<GateSig>();
        _response = new ChatResponse(new ChatMessage(ChatRole.Assistant,
            "[[ ## Answer ## ]]\nParis"));
    }

    [Benchmark]
    public IReadOnlyDictionary<string, object?> Parse() => _adapter.Parse(_meta, _response);
}

[MemoryDiagnoser]
[SimpleJob(launchCount: 3, warmupCount: 5, iterationCount: 10)]
[Config(typeof(InProcessConfig))]
public class JsonAdapterAllocGate
{
    JsonAdapter _adapter = null!;
    SignatureMetadata _meta = null!;
    ChatResponse _response = null!;

    [GlobalSetup]
    public void Setup()
    {
        _adapter = new JsonAdapter();
        _meta = SignatureMetadataCache.For<GateSig>();
        _response = new ChatResponse(new ChatMessage(ChatRole.Assistant,
            """{"answer":"Paris"}"""));
    }

    [Benchmark]
    public IReadOnlyDictionary<string, object?> Parse() => _adapter.Parse(_meta, _response);
}

internal sealed class InProcessConfig : ManualConfig
{
    public InProcessConfig()
    {
        AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance));
    }
}

internal sealed class GateNullChat(ChatResponse response) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> _, ChatOptions? __ = null, CancellationToken ___ = default)
        => Task.FromResult(response);
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> _, ChatOptions? __ = null, CancellationToken ___ = default)
        => throw new NotSupportedException();
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
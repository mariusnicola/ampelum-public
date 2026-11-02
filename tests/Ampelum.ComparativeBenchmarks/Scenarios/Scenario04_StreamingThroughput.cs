using SignalGrid.Ampelum;
using Ampelum.ComparativeBenchmarks.Competitors.Shared;
using Ampelum.ComparativeBenchmarks.Competitors.Sk;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;

namespace Ampelum.ComparativeBenchmarks.Scenarios;

/// <summary>
/// User-brief S4 — streaming throughput.
///
/// Plan name: Scenario04_StreamingThroughput.
/// Budget D7: parse path ≥100k chunks/sec on the bench machine. With 100 chunks
/// per Predict.StreamAsync that translates to ≤1 ms per Forward as the gate.
///
/// Compares Ampelum <see cref="Predict{TSig}.StreamAsync"/> (ChatAdapter parse
/// path) against raw MEAI <see cref="IChatClient.GetStreamingResponseAsync"/>
/// (no parse) and Semantic Kernel chat-streaming. The MEAI raw row is the
/// hard floor; Ampelum's row pays the marker-strip + field-route cost on top.
///
/// <para>Competitor coverage:</para>
/// SK exposes <c>IChatCompletionService.GetStreamingChatMessageContentsAsync</c>
/// which we route through the same <see cref="IChatClient"/> mock so the LM
/// cost is byte-identical (delta = SK orchestration only). AutoGen.NET 0.2.3
/// does not expose a documented streaming-iteration hook for <c>IAgent</c>
/// against an arbitrary <see cref="IChatClient"/>, so it is intentionally
/// omitted from this scenario; alpha.6 revisits if the harness lands.
/// LangChain.NET (tryAGI) streams via <c>IChatModel.GenerateAsync</c> events
/// — also deferred to keep this scenario apples-to-apples on parse path.
/// </summary>
[MemoryDiagnoser]
public class Scenario04_StreamingThroughput
{
    /// <summary>Chunks per stream — 100 keeps the bench tight while still amortising the
    /// per-stream setup cost so the per-chunk parse work dominates the measurement.</summary>
    [Params(100)]
    public int ChunkCount { get; set; }

    private ChunkedMockChatClient _mock = null!;
    private Predict<QaSig> _ampelum = null!;
    private Kernel _skKernel = null!;

    [GlobalSetup]
    public void Setup()
    {
        _mock = new ChunkedMockChatClient(
            "[[ ## Answer ## ]]\n42\n[[ ## completed ## ]]",
            ChunkCount);
        _ampelum = Predict.Create<QaSig>(_mock);
        _skKernel = SkStreaming.BuildKernel(_mock);
    }

    [Benchmark(Baseline = true)]
    public async Task<int> Ampelum()
    {
        int n = 0;
        await foreach (var _ in _ampelum.StreamAsync(new QaSig { Question = "q" }))
            n++;
        return n;
    }

    [Benchmark]
    public async Task<int> MeaiRaw()
    {
        int n = 0;
        await foreach (var _ in _mock.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "q")]))
            n++;
        return n;
    }

    [Benchmark]
    public async Task<int> Sk() => await SkStreaming.RunAsync(_skKernel, "q");
}

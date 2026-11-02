using SignalGrid.Ampelum;
using Ampelum.ComparativeBenchmarks.Competitors.Shared;
using Ampelum.ComparativeBenchmarks.Competitors.Sk;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;

namespace Ampelum.ComparativeBenchmarks.Scenarios;

/// <summary>
/// User-brief S2 — allocation per Forward.
///
/// Plan name: Scenario12_AllocationPerPredict (must-be-#1 per v1-decisions §B3).
/// Budget D3: Ampelum no-hooks ≤ 512 B / Forward; full pipeline ≤ 1024 B.
/// <see cref="MemoryDiagnoserAttribute"/> is the source of truth — the
/// "Allocated" column in the BDN summary is what the must-be-#1 ranker reads.
/// </summary>
[MemoryDiagnoser]
public class Scenario12_AllocationPerPredict
{
    private MockChatClient _mock = null!;
    private Predict<QaSig> _ampelum = null!;
    private Kernel _skKernel = null!;

    [GlobalSetup]
    public void Setup()
    {
        _mock = new MockChatClient("[[ ## Answer ## ]]\n42\n[[ ## completed ## ]]");
        _ampelum = Predict.Create<QaSig>(_mock);
        _skKernel = SkSinglePredict.BuildKernel(_mock);
    }

    [Benchmark(Baseline = true)]
    public async Task<Prediction<QaSig>> Ampelum()
        => await _ampelum.ForwardAsync(new QaSig { Question = "q" });

    [Benchmark]
    public async Task<ChatResponse> MeaiRaw()
        => await _mock.GetResponseAsync([new ChatMessage(ChatRole.User, "q")]);

    [Benchmark]
    public async Task<string> Sk() => await SkSinglePredict.RunAsync(_skKernel, "q");
}

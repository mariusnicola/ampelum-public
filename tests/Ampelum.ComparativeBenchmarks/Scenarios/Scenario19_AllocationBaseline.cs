using SignalGrid.Ampelum;
using Ampelum.ComparativeBenchmarks.Competitors.Ampelum;
using Ampelum.ComparativeBenchmarks.Competitors.Shared;
using Ampelum.ComparativeBenchmarks.Competitors.Sk;
using BenchmarkDotNet.Attributes;
using Microsoft.SemanticKernel;

namespace Ampelum.ComparativeBenchmarks.Scenarios;

[MemoryDiagnoser]
public class Scenario19_AllocationBaseline
{
    private MockChatClient _mock = null!;
    private Predict<ConciergeQaSig> _ampelum = null!;
    private Kernel _skKernel = null!;

    [GlobalSetup]
    public void Setup()
    {
        _mock = new MockChatClient("short");
        _ampelum = AmpelumConcierge.BuildPipeline(_mock);
        _skKernel = SkSinglePredict.BuildKernel(_mock);
    }

    [Benchmark(Baseline = true)]
    public async Task<Prediction<ConciergeQaSig>> Ampelum()
        => await AmpelumConcierge.RunAsync(_ampelum, "ctx", "q?");

    [Benchmark]
    public async Task<string> Sk() => await SkSinglePredict.RunAsync(_skKernel, "q?");
}

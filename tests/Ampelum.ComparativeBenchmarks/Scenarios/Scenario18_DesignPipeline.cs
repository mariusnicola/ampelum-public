using SignalGrid.Ampelum;
using Ampelum.ComparativeBenchmarks.Competitors.Ampelum;
using Ampelum.ComparativeBenchmarks.Competitors.Shared;
using Ampelum.ComparativeBenchmarks.Competitors.Sk;
using BenchmarkDotNet.Attributes;
using Microsoft.SemanticKernel;

namespace Ampelum.ComparativeBenchmarks.Scenarios;

[MemoryDiagnoser]
public class Scenario18_DesignPipeline
{
    private const string Brief = "A luxury travel agency -- modern, premium feel";
    private const string BrandKit = "Primary: #1A1A2E, Accent: #E94560, Font: Inter, Dark theme";

    private MockChatClient _mock = null!;
    private Predict<DesignGenSig> _ampelum = null!;
    private Kernel _skKernel = null!;

    [GlobalSetup]
    public void Setup()
    {
        _mock = new MockChatClient("<!DOCTYPE html><html><head><title>Luxury Travel</title></head><body><nav></nav><section>Hero</section><section>Destinations</section></body></html>");
        _ampelum = AmpelumDesign.BuildPipeline(_mock);
        _skKernel = SkSinglePredict.BuildKernel(_mock);
    }

    [Benchmark(Baseline = true)]
    public async Task<Prediction<DesignGenSig>> Ampelum()
        => await AmpelumDesign.RunAsync(_ampelum, Brief, BrandKit);

    [Benchmark]
    public async Task<string> Sk() => await SkSinglePredict.RunAsync(_skKernel, Brief);
}

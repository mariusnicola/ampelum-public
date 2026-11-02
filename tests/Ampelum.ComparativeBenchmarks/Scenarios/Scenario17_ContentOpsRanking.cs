using SignalGrid.Ampelum;
using Ampelum.ComparativeBenchmarks.Competitors.Ampelum;
using Ampelum.ComparativeBenchmarks.Competitors.Shared;
using Ampelum.ComparativeBenchmarks.Competitors.Sk;
using BenchmarkDotNet.Attributes;
using Microsoft.SemanticKernel;

namespace Ampelum.ComparativeBenchmarks.Scenarios;

[MemoryDiagnoser]
public class Scenario17_ContentOpsRanking
{
    private const string Topics = "cybersecurity thought leadership: zero-trust architecture, supply chain security, AI-powered threat detection, ransomware defense, compliance automation";

    private MockChatClient _mock = null!;
    private Predict<ContentOpsRankSig> _ampelum = null!;
    private Kernel _skKernel = null!;

    [GlobalSetup]
    public void Setup()
    {
        _mock = new MockChatClient("[[ ## Answer ## ]]\n1. AI-powered threat detection\n2. Zero-trust architecture\n3. Compliance automation\n[[ ## completed ## ]]");
        _ampelum = AmpelumContentOps.BuildPipeline(_mock);
        _skKernel = SkSinglePredict.BuildKernel(_mock);
    }

    [Benchmark(Baseline = true)]
    public async Task<Prediction<ContentOpsRankSig>> Ampelum()
        => await AmpelumContentOps.RunAsync(_ampelum, Topics);

    [Benchmark]
    public async Task<string> Sk() => await SkSinglePredict.RunAsync(_skKernel, Topics);
}

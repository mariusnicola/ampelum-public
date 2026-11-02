using SignalGrid.Ampelum;
using Ampelum.ComparativeBenchmarks.Competitors.Ampelum;
using Ampelum.ComparativeBenchmarks.Competitors.Shared;
using Ampelum.ComparativeBenchmarks.Competitors.Sk;
using BenchmarkDotNet.Attributes;
using Microsoft.SemanticKernel;

namespace Ampelum.ComparativeBenchmarks.Scenarios;

[MemoryDiagnoser]
public class Scenario16_ConciergeQa
{
    private const string Context = "SignalGrid offers healthcare compliance automation, patient data analytics, and clinical workflow optimization.";
    private const string Question = "What compliance services does SignalGrid offer?";

    private MockChatClient _mock = null!;
    private Predict<ConciergeQaSig> _ampelum = null!;
    private Kernel _skKernel = null!;

    [GlobalSetup]
    public void Setup()
    {
        _mock = new MockChatClient("[[ ## Answer ## ]]\nSignalGrid offers healthcare compliance automation.\n[[ ## completed ## ]]");
        _ampelum = AmpelumConcierge.BuildPipeline(_mock);
        _skKernel = SkSinglePredict.BuildKernel(_mock);
    }

    [Benchmark(Baseline = true)]
    public async Task<Prediction<ConciergeQaSig>> Ampelum()
        => await AmpelumConcierge.RunAsync(_ampelum, Context, Question);

    [Benchmark]
    public async Task<string> Sk() => await SkSinglePredict.RunAsync(_skKernel, Question);
}

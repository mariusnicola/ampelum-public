using SignalGrid.Ampelum;
using Ampelum.ComparativeBenchmarks.Competitors.AutoGenNet;
using Ampelum.ComparativeBenchmarks.Competitors.LangChainNet;
using Ampelum.ComparativeBenchmarks.Competitors.Shared;
using Ampelum.ComparativeBenchmarks.Competitors.Sk;
using AutoGen.Core;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;

namespace Ampelum.ComparativeBenchmarks.Scenarios;

/// <summary>
/// User-brief S1 — single Predict no-hooks orchestration overhead.
///
/// Plan name: Scenario01_SinglePredict (must-be-#1 per v1-decisions §B3).
/// Budget D1: Ampelum p50 ≤ MEAI raw + 500 ns and ≤ +256 B alloc.
///
/// <para>v1.0-rc1.alpha.4 update — real competitor harnesses replacing the
/// 2026-04-26-audit-flagged shims:</para>
/// <list type="bullet">
/// <item>SK row: real <see cref="Kernel"/> + <see cref="KernelExtensions.InvokePromptAsync(Kernel, string, KernelArguments?, string?, IPromptTemplateFactory?, PromptTemplateConfig?, CancellationToken)"/> over the same MEAI <see cref="IChatClient"/> mock.</item>
/// <item>AutoGen row: real <see cref="MiddlewareAgent"/> wrapping a custom <c>IAgent</c> that delegates to the same mock.</item>
/// <item>LangChain row: real <c>Chain.Set | Chain.Template | Chain.LLM</c> stack over a custom <c>ChatModel</c> that delegates to the same mock.</item>
/// </list>
/// <para>Python DSPy is wired separately via the subprocess harness.</para>
/// </summary>
[MemoryDiagnoser]
public class Scenario01_SinglePredict
{
    private MockChatClient _mock = null!;
    private Predict<QaSig> _ampelum = null!;
    private Kernel _skKernel = null!;
    private MiddlewareAgent _autoGenAgent = null!;
    private LangChainSinglePredict.LangChainHarness _langChain = null!;

    [GlobalSetup]
    public void Setup()
    {
        _mock = new MockChatClient("[[ ## Answer ## ]]\n42\n[[ ## completed ## ]]");
        _ampelum = Predict.Create<QaSig>(_mock);
        _skKernel = SkSinglePredict.BuildKernel(_mock);
        _autoGenAgent = AutoGenSinglePredict.BuildAgent(_mock);
        _langChain = LangChainSinglePredict.BuildChain(_mock);
    }

    [Benchmark(Baseline = true)]
    public async Task<Prediction<QaSig>> Ampelum()
        => await _ampelum.ForwardAsync(new QaSig { Question = "q" });

    [Benchmark]
    public async Task<ChatResponse> MeaiRaw()
        => await _mock.GetResponseAsync([new ChatMessage(ChatRole.User, "q")]);

    [Benchmark]
    public async Task<string> Sk() => await SkSinglePredict.RunAsync(_skKernel, "q");

    [Benchmark]
    public async Task<string> AutoGenNet() => await AutoGenSinglePredict.RunAsync(_autoGenAgent, "q");

    [Benchmark]
    public async Task<string> LangChainNet() => await LangChainSinglePredict.RunAsync(_langChain, "q");
}

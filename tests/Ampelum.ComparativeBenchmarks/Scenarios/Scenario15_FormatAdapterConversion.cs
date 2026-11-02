using SignalGrid.Ampelum.ProviderAdapters;
using BenchmarkDotNet.Attributes;

namespace Ampelum.ComparativeBenchmarks.Scenarios;

/// <summary>
/// User-brief S15 — provider tool-call format conversion.
///
/// Plan name: Scenario15_FormatAdapterConversion (must-be-#1 per §B3 — first/only).
/// No competitor in the pinned set ships a provider-neutral tool-call adapter; SK has
/// per-provider connectors but no public converter API, AutoGen.NET / LangChain.NET
/// hard-code OpenAI shape. The benchmark therefore measures Ampelum's
/// <see cref="ToolCallFormatConverter"/> end-to-end round-trip cost
/// (OpenAI → Anthropic → Gemini → OpenAI) plus a per-direction baseline.
///
/// <para>Honest reporting: the "competitor" rows on the ranker output emit "—"
/// for SK / AutoGen / LangChain because the operation has no equivalent;
/// see <c>docs/performance/comparative-results.md</c>.</para>
/// </summary>
[MemoryDiagnoser]
public class Scenario15_FormatAdapterConversion
{
    // Realistic parallel-call payload with nested arguments; mirrors the Pareto #15
    // shape used in the integration suite for parity with the verification gate.
    const string OpenAiInput = """
        [
          { "id": "call_1", "type": "function",
            "function": { "name": "search", "arguments": "{\"q\":\"weather in Paris\",\"opts\":{\"max\":5}}" } },
          { "id": "call_2", "type": "function",
            "function": { "name": "translate", "arguments": "{\"text\":\"hello\",\"to\":\"fr\"}" } }
        ]
        """;

    IReadOnlyList<NormalizedToolCall> _parsed = null!;
    string _anthropicJson = null!;
    string _geminiJson = null!;

    [GlobalSetup]
    public void Setup()
    {
        _parsed = ToolCallFormatConverter.FromOpenAi(OpenAiInput);
        _anthropicJson = ToolCallFormatConverter.ToAnthropic(_parsed);
        _geminiJson = ToolCallFormatConverter.ToGemini(_parsed);
    }

    /// <summary>Full triangular round-trip: OAI → Anthropic → Gemini → OAI.</summary>
    [Benchmark(Baseline = true)]
    public string Ampelum_OAI_Anthropic_Gemini_OAI()
    {
        var calls = ToolCallFormatConverter.FromOpenAi(OpenAiInput);
        var anth = ToolCallFormatConverter.ToAnthropic(calls);
        var fromAnth = ToolCallFormatConverter.FromAnthropic(anth);
        var gem = ToolCallFormatConverter.ToGemini(fromAnth);
        var fromGem = ToolCallFormatConverter.FromGemini(gem);
        return ToolCallFormatConverter.ToOpenAi(fromGem);
    }

    /// <summary>One-direction baseline: parse OAI only.</summary>
    [Benchmark]
    public int Ampelum_FromOpenAi() => ToolCallFormatConverter.FromOpenAi(OpenAiInput).Count;

    /// <summary>One-direction baseline: render OAI only.</summary>
    [Benchmark]
    public string Ampelum_ToOpenAi() => ToolCallFormatConverter.ToOpenAi(_parsed);

    /// <summary>Anthropic ⇄ normalized round-trip.</summary>
    [Benchmark]
    public string Ampelum_Anthropic_Roundtrip()
        => ToolCallFormatConverter.ToAnthropic(ToolCallFormatConverter.FromAnthropic(_anthropicJson));

    /// <summary>Gemini ⇄ normalized round-trip.</summary>
    [Benchmark]
    public string Ampelum_Gemini_Roundtrip()
        => ToolCallFormatConverter.ToGemini(ToolCallFormatConverter.FromGemini(_geminiJson));
}

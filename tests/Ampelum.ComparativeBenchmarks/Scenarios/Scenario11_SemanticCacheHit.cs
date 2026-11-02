using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Caching.Semantic;
using Ampelum.ComparativeBenchmarks.Competitors.Shared;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ampelum.ComparativeBenchmarks.Scenarios;

/// <summary>
/// User-brief S11 — semantic cache hit-path latency.
///
/// Plan name: Scenario11_SemanticCacheHit (must-be-#1 per §B3 — first/only).
///
/// Three rows:
/// <list type="bullet">
///   <item><b>Ampelum_Hit</b> — <see cref="Predict{TSig}.ForwardAsync"/> through a
///   pre-warmed <see cref="SemanticCacheHook"/>. The hook short-circuits the LM
///   call by populating <c>HookContext.Outputs</c>; this is the production hit path.</item>
///   <item><b>NoCache_Baseline</b> — same Predict, no cache hook, with the
///   <see cref="MockChatClient"/> returning the canned response. Measures the
///   floor of "what every forward costs without caching".</item>
///   <item><b>LangChain_*</b> — DEFERRED (see honesty note). The LangChain.NET 0.17.0
///   pinned package surfaces a memory cache primitive but the wiring to its
///   <c>IChatModel</c> equivalent through <see cref="Microsoft.Extensions.AI.IChatClient"/>
///   is not API-compatible with our shared mock. Rather than synthesise a row that
///   measures plumbing, the ranker emits "—" for LangChain in this scenario and
///   <c>docs/performance/comparative-results.md</c> documents the gap explicitly.</item>
/// </list>
///
/// <para>The Ampelum win story is that the hit path eliminates the LM call AND keeps
/// the audit chain semantically identical via metadata replay (<see cref="HookMetadataKeys.SemanticCacheHit"/>).
/// The benchmark only measures the latency half; chain-equivalence is verified in
/// <c>Pareto16</c> in the integration suite.</para>
/// </summary>
[MemoryDiagnoser]
public class Scenario11_SemanticCacheHit
{
    MockChatClient _mock = null!;
    Predict<QaSig> _withCache = null!;
    Predict<QaSig> _noCache = null!;
    QaSig _input = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _mock = new MockChatClient("[[ ## Answer ## ]]\n42\n[[ ## completed ## ]]");
        _input = new QaSig { Question = "q" };

        var cache = new InMemorySemanticCache(capacity: 8);
        var ctxCache = new AmpelumContext(
                _mock,
                new ChatAdapter(),
                NullTraceSink.Instance,
                NullLoggerFactory.Instance)
            .WithHook(new SemanticCacheHook(cache));
        _withCache = new Predict<QaSig>(ctxCache);

        // Pre-warm the cache: one forward registers the entry on miss.
        await _withCache.ForwardAsync(_input);

        var ctxNo = new AmpelumContext(
            _mock,
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance);
        _noCache = new Predict<QaSig>(ctxNo);
    }

    [Benchmark(Baseline = true)]
    public async Task<Prediction<QaSig>> Ampelum_Hit()
        => await _withCache.ForwardAsync(_input);

    [Benchmark]
    public async Task<Prediction<QaSig>> NoCache_Baseline()
        => await _noCache.ForwardAsync(_input);
}

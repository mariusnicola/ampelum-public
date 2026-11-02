using SignalGrid.Ampelum;
using Ampelum.ComparativeBenchmarks.Competitors.Shared;
using Ampelum.ComparativeBenchmarks.SubprocessHarness;
using SignalGrid.Ampelum.Optimizers;
using BenchmarkDotNet.Attributes;

namespace Ampelum.ComparativeBenchmarks.Scenarios;

/// <summary>
/// User-brief S8 — optimizer compile time.
///
/// Plan name: Scenario08_OptimizerCompile (must-be-#1 per v1-decisions §B3).
/// Budget D6: Ampelum BootstrapFewShot wall-clock ≤50% of the Python
/// reference for 50 examples.
///
/// Compares <see cref="BootstrapFewShot{TProgram}.CompileAsync"/> against the
/// Python reference's equivalent <c>BootstrapFewShot.compile</c> via
/// <see cref="PythonDspyRunner"/>. Both sides drive a deterministic mock LM
/// so the delta is pure optimizer machinery.
///
/// <para>Python row availability:</para>
/// If neither <c>uv</c>, <c>python</c>, nor <c>python3</c> is on PATH (and
/// <c>AMPELUM_COMPARATIVE_PY</c> is unset), the Python row short-circuits to
/// a sentinel result (elapsed=0, output="skipped: python unavailable") so
/// BenchmarkDotNet still emits a complete table — BDN does not support a
/// <c>[Skip]</c> attribute. Local devs missing Python see the Ampelum
/// number alone; CI installs uv so the row is always populated.
/// </summary>
[MemoryDiagnoser]
public class Scenario08_OptimizerCompile
{
    /// <summary>Trainset size — fixed at 50 per D6 budget.</summary>
    [Params(50)]
    public int Examples { get; set; }

    private MockChatClient _mock = null!;
    private AmpelumContext _ctx = null!;
    private List<Example> _trainset = null!;
    private BootstrapFewShot<Predict<QaSig>> _optimizer = null!;
    private PythonDspyRunner _pyRunner = null!;
    private bool _pyAvailable;

    [GlobalSetup]
    public void Setup()
    {
        _mock = new MockChatClient("[[ ## Answer ## ]]\n42\n[[ ## completed ## ]]");
        _ctx = new AmpelumContext(
            _mock,
            new ChatAdapter(),
            NullTraceSink.Instance,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        _trainset = new List<Example>(Examples);
        for (int i = 0; i < Examples; i++)
        {
            _trainset.Add(new Example(
                new Dictionary<string, object?> { ["Question"] = $"q{i}" },
                new Dictionary<string, object?> { ["Answer"] = "42" }));
        }

        _optimizer = new BootstrapFewShot<Predict<QaSig>>(
            programRunner: static async (program, example, ctx, ct) =>
            {
                using var scope = AmpelumContext.Push(ctx);
                var inputs = new QaSig
                {
                    Question = example.Inputs.TryGetValue("Question", out var q) && q is string s ? s : "",
                };
                await program.ForwardAsync(inputs, ct: ct);
            },
            maxBootstrappedDemos: 4,
            maxLabeledDemos: 16);

        _pyAvailable = PythonDspyRunner.IsAvailable;
        _pyRunner = new PythonDspyRunner
        {
            ScenarioId = "S8_bootstrap_fewshot_compile",
            Examples = Examples,
            MockResponses = ["42"],
            Seed = 0,
        };
    }

    [Benchmark(Baseline = true)]
    public async Task<OptimizedProgram<Predict<QaSig>>> Ampelum()
    {
        var program = new Predict<QaSig>(_ctx);
        return await _optimizer.CompileAsync(
            program,
            _trainset,
            new AlwaysOneMetric(),
            _ctx);
    }

    [Benchmark]
    public async Task<long> PythonReference()
    {
        if (!_pyAvailable) return 0L;
        var result = await _pyRunner.RunAsync();
        return result.ElapsedNs;
    }

    /// <summary>Trivial metric: always returns 1.0 so every bootstrap candidate qualifies as a demo.</summary>
    private sealed class AlwaysOneMetric : IMetric
    {
        public double Score(object actual, object expected, SignatureMetadata signature) => 1.0;
    }
}

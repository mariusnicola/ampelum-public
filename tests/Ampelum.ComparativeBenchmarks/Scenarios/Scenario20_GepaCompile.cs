using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Optimizers;
using Ampelum.ComparativeBenchmarks.Competitors.Shared;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ampelum.ComparativeBenchmarks.Scenarios;

[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 1, iterationCount: 3)]
public class Scenario20_GepaCompile
{
    [Signature("Given a question, produce a concise, accurate answer.")]
    public sealed record QASig(
        [Input] string Question,
        [Output] string Answer
    ) : ISignature;

    private MockChatClient _optimizerLlm = null!;
    private MockChatClient _programLlm = null!;
    private AmpelumContext _ctx = null!;
    private Predict<QASig> _program = null!;
    private GEPAOptimizer<Predict<QASig>> _optimizer = null!;
    private IReadOnlyList<Example> _trainset = null!;

    [GlobalSetup]
    public void Setup()
    {
        _optimizerLlm = new MockChatClient(
            "Provide clear, concise answers without unnecessary elaboration.");
        _programLlm = new MockChatClient(
            "[[ ## Answer ## ]]\nThe answer is correct.\n[[ ## completed ## ]]");

        _ctx = new AmpelumContext(
            _programLlm,
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance);

        _program = new Predict<QASig>(_ctx);
        _optimizer = new GEPAOptimizer<Predict<QASig>>(RunAsync, _optimizerLlm);

        _trainset = Enumerable.Range(0, 10)
            .Select(i => new Example(
                new Dictionary<string, object?> { ["Question"] = $"Question {i}?" },
                new Dictionary<string, object?> { ["Answer"] = $"Answer {i}" }))
            .ToArray();
    }

    [Benchmark(Baseline = true)]
    public async Task<Predict<QASig>> Baseline_Uncompiled()
    {
        var prediction = await _program.ForwardAsync(
            new QASig("What is the capital of France?", ""),
            demos: null,
            CancellationToken.None);
        return _program;
    }

    [Benchmark]
    public async Task<OptimizedProgram<Predict<QASig>>> GepaCompile_DefaultOptions()
    {
        var result = await _optimizer.CompileAsync(
            _program,
            _trainset,
            new ExactMatchMetric(),
            _ctx,
            ct: CancellationToken.None);
        return result;
    }

    [Benchmark]
    public async Task<OptimizedProgram<Predict<QASig>>> GepaCompile_AggressiveSearch()
    {
        var aggressive = new GEPAOptimizer<Predict<QASig>>(
            RunAsync, _optimizerLlm,
            new GepaOptions
            {
                MaxIterations = 20,
                MinibatchSize = 5,
                MergeEvery = 2
            });

        var result = await aggressive.CompileAsync(
            _program,
            _trainset,
            new ExactMatchMetric(),
            _ctx,
            ct: CancellationToken.None);
        return result;
    }

    static async Task<Example> RunAsync(
        Predict<QASig> program, Example example, AmpelumContext context, CancellationToken ct)
    {
        var prediction = await program.ForwardAsync(
            new QASig((string)example.Inputs["Question"]!, ""), demos: null, ct);
        return Example.From(prediction.Value);
    }

    sealed class ExactMatchMetric : IMetric
    {
        public double Score(object actual, object expected, SignatureMetadata signature) => 1.0;
    }
}

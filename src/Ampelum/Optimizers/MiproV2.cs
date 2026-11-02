using System.Diagnostics;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Optimizers;

/// <summary>
/// Simplified MIPROv2 â€” combines BootstrapFewShot demo capture with COPRO-style instruction
/// proposal in a multi-round loop. Each round runs <see cref="Copro{TProgram}"/> to propose
/// candidate instruction overrides, scores the candidate against the trainset via
/// <c>_programRunner</c>, and keeps the highest-scoring instruction set.
/// Advanced Bayesian heuristics (numTrials, seed, maxLabeledDemos) are deferred to v0.5d.
/// </summary>
/// <typeparam name="TProgram">The user program type.</typeparam>
public sealed class MiproV2<TProgram> : IOptimizer<TProgram>
    where TProgram : class
{
    static readonly SignatureMetadata _placeholderMeta =
        new(typeof(object), "", [], []);

    readonly Func<TProgram, Example, AmpelumContext, CancellationToken, Task<Example>> _programRunner;
    readonly IChatClient _proposerClient;
    readonly int _maxRounds;
    readonly int _candidatesPerSubmodule;
    readonly int _maxBootstrappedDemos;

    /// <summary>Maximum proposal rounds.</summary>
    public int MaxRounds => _maxRounds;

    /// <summary>Instruction candidates per submodule per round.</summary>
    public int CandidatesPerSubmodule => _candidatesPerSubmodule;

    /// <summary>Maximum bootstrapped demos per submodule.</summary>
    public int MaxBootstrappedDemos => _maxBootstrappedDemos;

    /// <summary>Construct a MIPROv2 optimizer.</summary>
    /// <param name="programRunner">Runs the program against an example, returning the produced outputs.</param>
    /// <param name="instructionProposerClient">Chat client used by the inner COPRO step.</param>
    /// <param name="maxRounds">Number of propose+score rounds.</param>
    /// <param name="candidatesPerSubmodule">Instruction candidates per submodule each round.</param>
    /// <param name="maxBootstrappedDemos">Bootstrap-phase demo cap per submodule.</param>
    public MiproV2(
        Func<TProgram, Example, AmpelumContext, CancellationToken, Task<Example>> programRunner,
        IChatClient instructionProposerClient,
        int maxRounds = 3,
        int candidatesPerSubmodule = 3,
        int maxBootstrappedDemos = 4)
    {
        ArgumentNullException.ThrowIfNull(programRunner);
        ArgumentNullException.ThrowIfNull(instructionProposerClient);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRounds, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(candidatesPerSubmodule, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBootstrappedDemos);
        _programRunner = programRunner;
        _proposerClient = instructionProposerClient;
        _maxRounds = maxRounds;
        _candidatesPerSubmodule = candidatesPerSubmodule;
        _maxBootstrappedDemos = maxBootstrappedDemos;
    }

    /// <inheritdoc />
    public async Task<OptimizedProgram<TProgram>> CompileAsync(
        TProgram program,
        IReadOnlyList<Example> trainset,
        IMetric metric,
        AmpelumContext context,
        IProgress<OptimizerProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(trainset);
        ArgumentNullException.ThrowIfNull(metric);
        ArgumentNullException.ThrowIfNull(context);

        ct.ThrowIfCancellationRequested();

        using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.optimizer.compile");
        activity?.SetTag("optimizer.type", nameof(MiproV2<TProgram>));
        activity?.SetTag("optimizer.trainset_size", trainset.Count);

        var sw = Stopwatch.StartNew();

        // Phase 1 â€” bootstrap demos via inner BootstrapFewShot.
        // Adapt the user-supplied program runner to the BootstrapFewShot ProgramRunner shape
        // (BootstrapFewShot uses the trace sink wired into the ctx, not the returned Example).
        var bootstrapper = new BootstrapFewShot<TProgram>(
            async (prog, ex, runCtx, runCt) =>
            {
                _ = await _programRunner(prog, ex, runCtx, runCt).ConfigureAwait(false);
            },
            maxBootstrappedDemos: _maxBootstrappedDemos);
        var bootstrapped = await bootstrapper.CompileAsync(
            program, trainset, metric, context, null, ct).ConfigureAwait(false);

        var bestInstructions =
            new Dictionary<string, string>(bootstrapped.InstructionOverrides);
        double bestScore = await ScoreAsync(bootstrapped, trainset, metric, context, ct).ConfigureAwait(false);

        // Phase 2 â€” multi-round instruction proposal via COPRO; pick highest-scoring candidate.
        // Forward the programRunner so COPRO actually executes candidates (rather than
        // falling back to the degenerate self-comparison eval mode).
        var copro = new Copro<TProgram>(_proposerClient, _candidatesPerSubmodule, _programRunner);
        for (int round = 0; round < _maxRounds; round++)
        {
            ct.ThrowIfCancellationRequested();

            var candidate = await copro.CompileAsync(
                bootstrapped.Program, trainset, metric, context, null, ct).ConfigureAwait(false);

            double candidateScore = await ScoreAsync(candidate, trainset, metric, context, ct).ConfigureAwait(false);

            if (candidateScore > bestScore)
            {
                bestScore = candidateScore;
                bestInstructions = new Dictionary<string, string>(candidate.InstructionOverrides);
            }

            progress?.Report(new OptimizerProgress(
                Phase: $"round {round + 1}/{_maxRounds}",
                Step: round + 1,
                TotalSteps: _maxRounds,
                BestScore: bestScore));
        }

        sw.Stop();
        Diagnostics.OptimizerCompileDuration.Record(sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("optimizer.type", nameof(MiproV2<TProgram>)));
        Diagnostics.OptimizerCompileExamples.Record(trainset.Count,
            new KeyValuePair<string, object?>("optimizer.type", nameof(MiproV2<TProgram>)));
        Diagnostics.OptimizerCompileScore.Record(bestScore,
            new KeyValuePair<string, object?>("optimizer.type", nameof(MiproV2<TProgram>)));

        activity?.SetTag("optimizer.final_score", bestScore);

        return bootstrapped with
        {
            InstructionOverrides = bestInstructions,
            Score = bestScore,
        };
    }

    async Task<double> ScoreAsync(
        OptimizedProgram<TProgram> optimized,
        IReadOnlyList<Example> trainset,
        IMetric metric,
        AmpelumContext ctx,
        CancellationToken ct)
    {
        if (trainset.Count == 0) return 0.0;
        var bound = optimized.Bind(ctx);
        double sum = 0.0;
        foreach (var ex in trainset)
        {
            ct.ThrowIfCancellationRequested();
            var actual = await _programRunner(bound, ex, ctx, ct).ConfigureAwait(false);
            sum += metric.Score(actual.Outputs, ex.Outputs, _placeholderMeta);
        }
        return sum / trainset.Count;
    }
}

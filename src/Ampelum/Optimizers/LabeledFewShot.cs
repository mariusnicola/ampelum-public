using System.Diagnostics;

namespace SignalGrid.Ampelum.Optimizers;

/// <summary>
/// Simplest Ampelum optimizer: assigns the first <see cref="MaxDemos"/> examples from the trainset
/// as <c>DefaultDemos</c> for each submodule (or the single module). No LM calls, no evaluation.
/// </summary>
/// <typeparam name="TProgram">The user program type.</typeparam>
public sealed class LabeledFewShot<TProgram>(int maxDemos = 16) : IOptimizer<TProgram>
{
    /// <summary>Maximum number of demos to assign per submodule.</summary>
    public int MaxDemos { get; } = maxDemos;

    /// <inheritdoc/>
    public Task<OptimizedProgram<TProgram>> CompileAsync(
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

        var sw = Stopwatch.StartNew();
        ct.ThrowIfCancellationRequested();

        var demoCap = Math.Min(MaxDemos, trainset.Count);
        var demos = trainset.Take(demoCap).ToList();

        var demosBySubmodule = new Dictionary<string, IReadOnlyList<Example>>();

        if (program is IComposedModule composed)
        {
            foreach (var name in composed.Submodules.Keys)
                demosBySubmodule[name] = demos;
        }
        else
        {
            demosBySubmodule["__single__"] = demos;
        }

        sw.Stop();
        Diagnostics.OptimizerCompileDuration.Record(sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("optimizer.type", nameof(LabeledFewShot<TProgram>)));
        Diagnostics.OptimizerCompileExamples.Record(trainset.Count,
            new KeyValuePair<string, object?>("optimizer.type", nameof(LabeledFewShot<TProgram>)));

        progress?.Report(new OptimizerProgress("Done", 1, 1, 0.0));

        var result = new OptimizedProgram<TProgram>(
            program,
            demosBySubmodule,
            new Dictionary<string, string>())
        {
            TrainsetSize = trainset.Count,
        };

        return Task.FromResult(result);
    }
}

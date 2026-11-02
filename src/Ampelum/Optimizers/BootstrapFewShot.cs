using System.Diagnostics;

namespace SignalGrid.Ampelum.Optimizers;

/// <summary>
/// Optimizer that bootstraps few-shot demonstrations by running the program on the trainset,
/// capturing per-submodule traces via <c>CapturingTraceSink</c>, and assembling the
/// captured inputs+outputs as <c>DefaultDemos</c>.
///
/// Trace-to-submodule partitioning key: <c>(ModuleName, SignatureType)</c>.
/// Ambiguity (multiple submodules with same key) is resolved by execution order â€”
/// the first matching submodule name wins.
/// </summary>
/// <typeparam name="TProgram">The user program type.</typeparam>
public sealed class BootstrapFewShot<TProgram>(
    BootstrapFewShot<TProgram>.ProgramRunner programRunner,
    int maxBootstrappedDemos = 4,
    int maxLabeledDemos = 16) : IOptimizer<TProgram>
{
    /// <summary>
    /// Delegate invoked once per trainset example. Implementations should call the program's
    /// forward pass inside <paramref name="ctx"/> so traces are captured by the
    /// <c>CapturingTraceSink</c> wired into <paramref name="ctx"/>.
    /// </summary>
    public delegate Task ProgramRunner(
        TProgram program,
        Example example,
        AmpelumContext ctx,
        CancellationToken ct);

    /// <summary>Maximum bootstrapped demos per submodule.</summary>
    public int MaxBootstrappedDemos { get; } = maxBootstrappedDemos;

    /// <summary>Maximum labeled (trainset) demos per submodule (fallback if bootstrap yields fewer).</summary>
    public int MaxLabeledDemos { get; } = maxLabeledDemos;

    /// <inheritdoc/>
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

        using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.optimizer.compile");
        activity?.SetTag("optimizer.type", nameof(BootstrapFewShot<TProgram>));
        activity?.SetTag("optimizer.trainset_size", trainset.Count);

        var sw = Stopwatch.StartNew();

        var partitions = new Dictionary<(string ModuleName, Type SigType), List<TraceRecord>>();

        for (int i = 0; i < trainset.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var capturingSink = new InternalCapturingSink();
            var capturingCtx = context with { TraceSink = capturingSink };

            try
            {
                await programRunner(program, trainset[i], capturingCtx, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Failed runs don't contribute demos â€” continue.
            }

            foreach (var trace in capturingSink.Records)
            {
                if (trace.Error is not null) continue;

                var key = (trace.ModuleName, trace.SignatureType);
                if (!partitions.TryGetValue(key, out var list))
                    partitions[key] = list = [];
                list.Add(trace);
            }

            progress?.Report(new OptimizerProgress(
                "Trace",
                Step: i + 1,
                TotalSteps: trainset.Count,
                BestScore: 0.0));
        }

        var demosBySubmodule = new Dictionary<string, IReadOnlyList<Example>>();

        foreach (var kvp in partitions)
        {
            var moduleName = kvp.Key.ModuleName;
            var traces = kvp.Value;
            var bootstrapped = traces
                .Take(MaxBootstrappedDemos)
                .Select(t => new Example(t.Inputs, t.Outputs))
                .ToList();

            // partition lists are only created when a non-error trace is added,
            // so bootstrapped is guaranteed to be non-empty here.
            demosBySubmodule.TryAdd(moduleName, bootstrapped);
        }

        if (demosBySubmodule.Count == 0)
        {
            var labeled = trainset.Take(MaxLabeledDemos).ToList();
            if (program is IComposedModule composed)
            {
                foreach (var name in composed.Submodules.Keys)
                    demosBySubmodule[name] = labeled;
            }
            else
            {
                demosBySubmodule["__single__"] = labeled;
            }
        }

        sw.Stop();
        Diagnostics.OptimizerCompileDuration.Record(sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("optimizer.type", nameof(BootstrapFewShot<TProgram>)));
        Diagnostics.OptimizerCompileExamples.Record(trainset.Count,
            new KeyValuePair<string, object?>("optimizer.type", nameof(BootstrapFewShot<TProgram>)));

        activity?.SetTag("optimizer.final_score", 0.0);

        return new OptimizedProgram<TProgram>(
            program,
            demosBySubmodule,
            new Dictionary<string, string>())
        {
            TrainsetSize = trainset.Count,
        };
    }

    sealed class InternalCapturingSink : ITraceSink
    {
        readonly List<TraceRecord> _records = [];
        public void Write(TraceRecord record) => _records.Add(record);
        public IReadOnlyList<TraceRecord> Records => _records;
    }
}

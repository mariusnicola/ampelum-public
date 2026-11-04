using System.Diagnostics;
using SignalGrid.Ampelum.Exceptions;

namespace SignalGrid.Ampelum.Optimizers;

/// <summary>
/// Optimizer that bootstraps few-shot demonstrations by running the program on the trainset,
/// capturing per-submodule traces via <c>CapturingTraceSink</c>, and assembling the
/// captured inputs+outputs as <c>DefaultDemos</c>.
///
/// Trace-to-submodule partitioning key: <c>(ModuleName, SignatureType)</c>.
/// Ambiguity (multiple submodules with same key) is resolved by execution order â€”
/// the first matching submodule name wins.
///
/// A run becomes demos only when the metric scores the run's final outputs (the outputs of its last trace)
/// against the example's expected outputs at or above <c>metricThreshold</c>. A passing run keeps the trace of
/// every submodule, so the intermediate modules of a composed program get demos too. A metric that throws
/// counts as a fail.
/// </summary>
/// <typeparam name="TProgram">The user program type.</typeparam>
/// <param name="programRunner">Runs the program on one example inside the given context.</param>
/// <param name="maxBootstrappedDemos">Maximum bootstrapped demos per submodule.</param>
/// <param name="maxLabeledDemos">Maximum labeled demos per submodule when no trace passes.</param>
/// <param name="metricThreshold">Lowest metric score a run needs for its traces to be kept. Default 1.0, for a metric in [0, 1].</param>
public sealed class BootstrapFewShot<TProgram>(
    BootstrapFewShot<TProgram>.ProgramRunner programRunner,
    int maxBootstrappedDemos = 4,
    int maxLabeledDemos = 16,
    double metricThreshold = 1.0) : IOptimizer<TProgram>
{
    static readonly SignatureMetadata _placeholderMeta =
        new(typeof(object), "", [], []);

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

    /// <summary>Lowest metric score a trace needs to become a demo.</summary>
    public double MetricThreshold { get; } = metricThreshold;

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
        var failedRuns = 0;
        Exception? firstError = null;

        for (int i = 0; i < trainset.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var capturingSink = new InternalCapturingSink();
            var capturingCtx = context with { TraceSink = capturingSink };

            bool runFailed = false;
            try
            {
                await programRunner(program, trainset[i], capturingCtx, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                // A failed run contributes no demos and the others carry on; if all of them fail it is reported below.
                runFailed = true;
                failedRuns++;
                firstError ??= e;
            }

            // The run is scored once; a run that passes keeps every trace of every submodule. A run whose
            // runner threw has partial traces and is not scored.
            var passing = runFailed
                ? Array.Empty<TraceRecord>()
                : PassingTraces(capturingSink.Records, trainset[i], metric, MetricThreshold, out _);
            foreach (var trace in passing)
            {
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

        // Some failed runs are tolerated, but a runner that fails on every example (a Predict with no bound context,
        // say) would otherwise end as labeled demos with no sign of the cause.
        if (failedRuns > 0 && failedRuns == trainset.Count)
            throw new OptimizerException(
                $"BootstrapFewShot: the program runner failed on all {trainset.Count} examples. First error: {firstError!.Message}",
                firstError);

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

    /// <summary>
    /// The traces of one run that may become demos or training data: none when the run fails, otherwise every
    /// trace without an error. A run is scored once, on the outputs of its last trace (the final module call)
    /// against the example's expected outputs. A run with no trace, an errored last trace, a score below
    /// <paramref name="threshold"/> (NaN included) or a metric that throws fails; <paramref name="metricThrew"/>
    /// tells the last case apart. Cancellation propagates.
    /// </summary>
    internal static IReadOnlyList<TraceRecord> PassingTraces(
        IReadOnlyList<TraceRecord> records, Example example, IMetric metric, double threshold, out bool metricThrew)
    {
        metricThrew = false;
        if (records.Count == 0 || records[^1].Error is not null) return [];
        try
        {
            if (!(metric.Score(records[^1].Outputs, example.Outputs, _placeholderMeta) >= threshold)) return [];
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            metricThrew = true;
            return [];
        }
        return records.Where(r => r.Error is null).ToList();
    }

    sealed class InternalCapturingSink : ITraceSink
    {
        readonly List<TraceRecord> _records = [];
        public void Write(TraceRecord record) => _records.Add(record);
        public IReadOnlyList<TraceRecord> Records => _records;
    }
}

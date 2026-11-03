using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Exceptions;

namespace SignalGrid.Ampelum.Optimizers;

/// <summary>
/// BootstrapFinetune weight optimizer (as in <c>dspy.BootstrapFinetune</c>).
/// <list type="number">
///   <item><description>
///     The teacher (the program itself, or the program given to the constructor) runs on every trainset
///     example through the runner. The runner must write the trace of each module call to the
///     <see cref="AmpelumContext.TraceSink"/> of the context it receives, as for <see cref="BootstrapFewShot{TProgram}"/>.
///   </description></item>
///   <item><description>
///     A run is scored once: the metric scores the outputs of the run's last trace against the example's expected
///     outputs. A run at or above <c>metricThreshold</c> keeps every trace without an error, for every module.
///     Kept traces are grouped by module name.
///   </description></item>
///   <item><description>
///     For each module with a kept trace the training data is built (see <see cref="FineTuneRequest"/>) and
///     <see cref="IFineTuneProvider"/> is called once. The program that is returned sends that module's calls
///     to the tuned client: <see cref="OptimizedProgram{TProgram}.Bind"/> applies <c>ChatClients</c>.
///   </description></item>
/// </list>
/// A training example is the messages the module sent in that call (instruction override and demos included)
/// followed by its response as the assistant message.
/// A runner or metric exception scores that run or trace as a fail and is counted (activity tag
/// <c>optimizer.runner_errors</c> and the <c>RunnerErrors</c> extra of every progress report). Only cancellation of
/// the token passed to <c>CompileAsync</c> propagates; a failing provider throws <see cref="OptimizerException"/>.
/// The result has no <c>Score</c>. The library ships no provider.
/// </summary>
/// <typeparam name="TProgram">The user program type.</typeparam>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class BootstrapFinetune<TProgram> : IOptimizer<TProgram>
    where TProgram : class
{
    readonly BootstrapFewShot<TProgram>.ProgramRunner _programRunner;
    readonly IFineTuneProvider _provider;
    readonly TProgram? _teacher;

    /// <summary>Lowest metric score a trace needs to be kept.</summary>
    public double MetricThreshold { get; }

    /// <summary>Construct a BootstrapFinetune optimizer.</summary>
    /// <param name="programRunner">Runs the teacher on one example inside the given context.</param>
    /// <param name="provider">Fine-tunes a model on the training data of one submodule.</param>
    /// <param name="teacher">Program to run on the trainset. Default: the program passed to <c>CompileAsync</c>.</param>
    /// <param name="metricThreshold">Lowest metric score a trace needs to be kept. Default 1.0, for a metric in [0, 1].</param>
    public BootstrapFinetune(
        BootstrapFewShot<TProgram>.ProgramRunner programRunner,
        IFineTuneProvider provider,
        TProgram? teacher = null,
        double metricThreshold = 1.0)
    {
        ArgumentNullException.ThrowIfNull(programRunner);
        ArgumentNullException.ThrowIfNull(provider);
        _programRunner = programRunner;
        _provider = provider;
        _teacher = teacher;
        MetricThreshold = metricThreshold;
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
        activity?.SetTag("optimizer.type", nameof(BootstrapFinetune<TProgram>));
        activity?.SetTag("optimizer.trainset_size", trainset.Count);

        var sw = Stopwatch.StartNew();
        var teacher = _teacher ?? program;
        var kept = new OrderedDictionary<string, List<TraceRecord>>();
        int errors = 0;
        int keptCount = 0;

        for (int i = 0; i < trainset.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var sink = new CapturingSink();
            try
            {
                await _programRunner(teacher, trainset[i], context with { TraceSink = sink }, ct).ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // A timeout cancels the call without cancelling ct; it is a failed run like any other.
                errors++;
                Report("teacher", i + 1, trainset.Count);
                continue;
            }

            var passing = BootstrapFewShot<TProgram>.PassingTraces(
                sink.Records, trainset[i], metric, MetricThreshold, out var metricThrew);
            if (metricThrew) errors++;

            foreach (var trace in passing)
            {
                if (!kept.TryGetValue(trace.ModuleName, out var list))
                    kept[trace.ModuleName] = list = [];
                list.Add(trace);
                keptCount++;
            }
            Report("teacher", i + 1, trainset.Count);
        }

        var clients = new Dictionary<string, IChatClient>();
        int done = 0;
        foreach (var (name, traces) in kept)
        {
            ct.ThrowIfCancellationRequested();

            var request = new FineTuneRequest(name, traces.Select(ToChat).ToList());
            try
            {
                clients[name] = await _provider.FineTuneAsync(request, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                throw new OptimizerException($"BootstrapFinetune provider failed for submodule '{name}'.", ex);
            }
            Report("finetune", ++done, kept.Count);
        }

        sw.Stop();
        Diagnostics.OptimizerCompileDuration.Record(sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("optimizer.type", nameof(BootstrapFinetune<TProgram>)));
        Diagnostics.OptimizerCompileExamples.Record(trainset.Count,
            new KeyValuePair<string, object?>("optimizer.type", nameof(BootstrapFinetune<TProgram>)));
        activity?.SetTag("optimizer.runner_errors", errors);
        activity?.SetTag("optimizer.kept_traces", keptCount);

        return new OptimizedProgram<TProgram>(
            program,
            new Dictionary<string, IReadOnlyList<Example>>(),
            new Dictionary<string, string>())
        {
            TrainsetSize = trainset.Count,
            ChatClients = clients,
        };

        void Report(string phase, int step, int total) =>
            progress?.Report(new OptimizerProgress(
                phase, step, total, BestScore: 0.0,
                Extras: new Dictionary<string, object?>
                {
                    ["RunnerErrors"] = errors,
                    ["KeptTraces"] = keptCount,
                }));
    }

    // The messages the module really sent (instruction override and demos included), then its response.
    static List<ChatMessage> ToChat(TraceRecord trace)
    {
        var messages = trace.PromptMessages.ToList();
        messages.Add(new ChatMessage(ChatRole.Assistant, trace.RawResponse));
        return messages;
    }

    sealed class CapturingSink : ITraceSink
    {
        readonly List<TraceRecord> _records = [];
        public void Write(TraceRecord record) => _records.Add(record);
        public IReadOnlyList<TraceRecord> Records => _records;
    }
}

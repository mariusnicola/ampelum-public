// src/Ampelum/Evaluate.cs
using System.Diagnostics;
using SignalGrid.Ampelum.Metrics;

namespace SignalGrid.Ampelum;

/// <summary>
/// Runs a prediction program over labelled examples, scoring each via <see cref="IMetric{TSig}"/>.
/// Thread-safe. Does not mutate the supplied program. Failures in the program delegate are
/// collected (not fatal); failures in the metric are rethrown.
/// </summary>
public sealed partial class Evaluate<TSig> where TSig : ISignature
{
    readonly Func<TSig, CancellationToken, Task<Prediction<TSig>>> _program;
    readonly IMetric<TSig> _metric;
    readonly int _maxDop;
    readonly int _failureSampleCount;
    readonly IProgress<EvaluationProgress>? _progress;
    readonly SignatureMetadata _meta;
    readonly Ampelum.Budget.IBudget? _budget;

    /// <summary>
    /// Initializes a new <see cref="Evaluate{TSig}"/> instance.
    /// </summary>
    /// <param name="program">The prediction program to evaluate. Must be thread-safe.</param>
    /// <param name="metric">The metric used to score each prediction. Must be stateless and thread-safe.</param>
    /// <param name="maxDegreeOfParallelism">Maximum number of examples evaluated concurrently. Default: 8.</param>
    /// <param name="failureSampleCount">Maximum number of lowest-scoring examples retained in <see cref="EvaluationResult{TSig}.FailureSamples"/>. Default: 10.</param>
    /// <param name="progress">Optional progress sink; receives one report per completed example.</param>
    public Evaluate(
        Func<TSig, CancellationToken, Task<Prediction<TSig>>> program,
        IMetric<TSig> metric,
        int maxDegreeOfParallelism = 8,
        int failureSampleCount = 10,
        IProgress<EvaluationProgress>? progress = null)
        : this(program, metric, maxDegreeOfParallelism, failureSampleCount, progress, budget: null) { }

    /// <summary>
    /// Initializes a new <see cref="Evaluate{TSig}"/> with optional budget tracking.
    /// </summary>
    /// <param name="program">The prediction program to evaluate.</param>
    /// <param name="metric">Score metric.</param>
    /// <param name="maxDegreeOfParallelism">Maximum concurrent examples.</param>
    /// <param name="failureSampleCount">Maximum lowest-scoring examples to retain.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="budget">
    /// Optional <see cref="Ampelum.Budget.IBudget"/>. When supplied, <c>Consume</c> is invoked once
    /// per successful prediction (between prediction and scoring). A
    /// <see cref="Ampelum.Budget.BudgetExceededException"/> propagates and aborts the run.
    /// </param>
    public Evaluate(
        Func<TSig, CancellationToken, Task<Prediction<TSig>>> program,
        IMetric<TSig> metric,
        int maxDegreeOfParallelism,
        int failureSampleCount,
        IProgress<EvaluationProgress>? progress,
        Ampelum.Budget.IBudget? budget)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(metric);
        _program = program;
        _metric = metric;
        _maxDop = maxDegreeOfParallelism;
        _failureSampleCount = failureSampleCount;
        _progress = progress;
        _budget = budget;
        _meta = SignatureMetadataCache.For<TSig>();
    }

    /// <summary>
    /// Runs the program over all <paramref name="examples"/> and returns an <see cref="EvaluationResult{TSig}"/>
    /// containing per-example scores, aggregate statistics, and failure samples.
    /// </summary>
    /// <param name="examples">Labelled examples to evaluate. May be empty.</param>
    /// <param name="ct">Cancellation token. Cancellation propagates and aborts all pending items.</param>
    /// <returns>Aggregate evaluation result.</returns>
    public async Task<EvaluationResult<TSig>> RunAsync(
        IReadOnlyList<LabeledExample<TSig>> examples,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(examples);

        using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.evaluate.run");
        activity?.SetTag("eval.example_count", examples.Count);
        activity?.SetTag("eval.parallelism", _maxDop);

        var sw = Stopwatch.StartNew();
        var scores = new ExampleScore<TSig>[examples.Count];
        var completed = 0;
        double runningSum = 0;
        var runningLock = new object();

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = _maxDop,
            CancellationToken = ct,
        };

        if (examples.Count > 0)
        {
            await Parallel.ForEachAsync(Enumerate(examples), options, async (item, t) =>
            {
                var (idx, ex) = item;
                Prediction<TSig>? prediction = null;
                Exception? error = null;
                double score = 0;

                try
                {
                    prediction = await _program(ex.Inputs, t).ConfigureAwait(false);
                }
                catch (Exception caught) when (caught is not OperationCanceledException)
                {
                    error = caught;
                }

                if (error is null && prediction is not null && _budget is not null)
                {
                    var u = prediction.Trace.Usage;
                    var tokens = (u?.InputTokenCount ?? 0) + (u?.OutputTokenCount ?? 0);
                    _budget.Consume(tokens, costCents: 0m, calls: 1);
                }

                if (error is null && prediction is not null)
                {
                    score = _metric.Score(prediction.Value, ex.Expected, _meta);
                    if (score < 0.0 || score > 1.0)
                        throw new InvalidOperationException(
                            $"Metric '{_metric.GetType().Name}' returned {score}, outside required range [0.0, 1.0].");
                }

                scores[idx] = new ExampleScore<TSig>(ex, prediction, score, error);

                int done; double mean;
                lock (runningLock)
                {
                    runningSum += score;
                    completed++;
                    done = completed;
                    mean = runningSum / done;
                }
                _progress?.Report(new EvaluationProgress(done, examples.Count, mean));
            }).ConfigureAwait(false);
        }

        sw.Stop();

        double sum = 0, sumSq = 0;
        int failureCount = 0;
        foreach (var s in scores)
        {
            sum += s.Score;
            sumSq += s.Score * s.Score;
            if (s.Error is not null) failureCount++;
        }
        var n = scores.Length;
        var mean2 = n == 0 ? 0.0 : sum / n;
        var stdDev = n <= 1 ? 0.0 : Math.Sqrt(Math.Max(0.0, sumSq / n - mean2 * mean2));

        var failureSamples = scores.OrderBy(s => s.Score).Take(_failureSampleCount).ToList();

        activity?.SetTag("eval.final_mean", mean2);
        activity?.SetTag("eval.failure_count", failureCount);
        Diagnostics.EvaluateScore.Record(mean2);
        Diagnostics.EvaluateDuration.Record(sw.Elapsed.TotalMilliseconds);
        if (failureCount > 0) Diagnostics.EvaluateFailures.Add(failureCount);

        return new EvaluationResult<TSig>(scores, mean2, stdDev, failureCount, failureSamples);
    }

    static IEnumerable<(int, LabeledExample<TSig>)> Enumerate(IReadOnlyList<LabeledExample<TSig>> src)
    {
        for (var i = 0; i < src.Count; i++) yield return (i, src[i]);
    }
}

/// <summary>
/// Progress snapshot emitted after each example completes during <see cref="Evaluate{TSig}.RunAsync(IReadOnlyList{LabeledExample{TSig}}, CancellationToken)"/>.
/// </summary>
/// <param name="Completed">Number of examples that have completed so far (including failures).</param>
/// <param name="Total">Total number of examples being evaluated.</param>
/// <param name="RunningMean">Running mean score across all completed examples.</param>
public sealed record EvaluationProgress(int Completed, int Total, double RunningMean);

/// <summary>
/// Score record for a single evaluated example, including the prediction result (or null on failure)
/// and any exception captured from the program delegate.
/// </summary>
/// <typeparam name="TSig">The signature type being evaluated.</typeparam>
/// <param name="Example">The labelled example that was evaluated.</param>
/// <param name="Prediction">The prediction returned by the program, or <see langword="null"/> if the program threw.</param>
/// <param name="Score">The metric score in <c>[0.0, 1.0]</c>, or <c>0.0</c> on failure.</param>
/// <param name="Error">The exception thrown by the program delegate, or <see langword="null"/> on success.</param>
public sealed record ExampleScore<TSig>(
    LabeledExample<TSig> Example,
    Prediction<TSig>? Prediction,
    double Score,
    Exception? Error)
    where TSig : ISignature;

/// <summary>
/// Aggregate result from <see cref="Evaluate{TSig}.RunAsync(IReadOnlyList{LabeledExample{TSig}}, CancellationToken)"/>, containing per-example scores
/// and summary statistics.
/// </summary>
/// <typeparam name="TSig">The signature type that was evaluated.</typeparam>
/// <param name="Scores">All per-example score records, in evaluation order.</param>
/// <param name="Mean">Arithmetic mean of all scores (0.0 when no examples).</param>
/// <param name="StdDev">Population standard deviation (0.0 when fewer than two examples).</param>
/// <param name="FailureCount">Number of examples where the program delegate threw.</param>
/// <param name="FailureSamples">Up to <c>failureSampleCount</c> lowest-scoring examples, sorted ascending by score.</param>
public sealed record EvaluationResult<TSig>(
    IReadOnlyList<ExampleScore<TSig>> Scores,
    double Mean,
    double StdDev,
    int FailureCount,
    IReadOnlyList<ExampleScore<TSig>> FailureSamples)
    where TSig : ISignature
{
    /// <summary>All examples where <see cref="ExampleScore{TSig}.Error"/> is non-null.</summary>
    public IReadOnlyList<ExampleScore<TSig>> Failures =>
        Scores.Where(s => s.Error is not null).ToList();
}

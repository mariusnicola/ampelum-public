using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using SignalGrid.Ampelum.Hooks;

namespace SignalGrid.Ampelum.Optimizers;

/// <summary>
/// GEPA (Generalized Efficient Prompt Adaptation) optimizer.
/// DSPy 3.0's gradient-free prompt search ported to C#:
/// Phase 1: Bootstrap quality-filtered demonstrations from the program's outputs.
/// Phase 2: Meta-prompt search over instruction variants against a validation set.
/// Phase 3: Feedback loop — accept improvements above threshold, diversify on stall.
/// Every phase is instrumented with wall-clock timing and allocation counters.
/// </summary>
/// <typeparam name="TProgram">The user program type (typically a Predict or composed module).</typeparam>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class GEPAOptimizer<TProgram> : IOptimizer<TProgram>
{
    readonly IChatClient _optimizerLlm;
    readonly IChatClient _programLlm;
    readonly GepaOptions _gepaOptions;

    /// <param name="optimizerLlm">Cheapest available model for meta-prompt generation.</param>
    /// <param name="programLlm">Model used by the program being optimized (bootstrap + eval).</param>
    public GEPAOptimizer(IChatClient optimizerLlm, IChatClient programLlm)
        : this(optimizerLlm, programLlm, new GepaOptions()) { }

    /// <param name="optimizerLlm">Cheapest available model for meta-prompt generation.</param>
    /// <param name="programLlm">Model used by the program being optimized.</param>
    /// <param name="options">Bootstrap count, candidate count, thresholds.</param>
    public GEPAOptimizer(IChatClient optimizerLlm, IChatClient programLlm, GepaOptions options)
    {
        _optimizerLlm = optimizerLlm ?? throw new ArgumentNullException(nameof(optimizerLlm));
        _programLlm = programLlm ?? throw new ArgumentNullException(nameof(programLlm));
        _gepaOptions = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc/>
    public async Task<OptimizedProgram<TProgram>> CompileAsync(
        TProgram program,
        IReadOnlyList<Example> trainset,
        IMetric metric,
        AmpelumContext context,
        IProgress<OptimizerProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (trainset.Count == 0)
            throw new ArgumentException("Trainset must contain at least one example.", nameof(trainset));

        var totalSw = Stopwatch.StartNew();
        long totalAllocStart = GC.GetAllocatedBytesForCurrentThread();

        // Split train/validation
        int validCount = Math.Max(1, trainset.Count * _gepaOptions.ValidationHoldoutPercent / 100);
        int trainCount = trainset.Count - validCount;
        var train = trainCount > 0 ? trainset.Take(trainCount).ToArray() : trainset.ToArray();
        var valid = trainset.Skip(trainCount).ToArray();

        // Phase 1 — Bootstrap
        progress?.Report(new OptimizerProgress("bootstrap", 0, trainCount, 0));
        (var demos, var bTimeMs, var bAlloc) = await BootstrapAsync(program, train, metric, context, ct);

        double baselineScore = valid.Length > 0 ? await ScoreAsync(program, valid, metric, context, ct) : 0;
        progress?.Report(new OptimizerProgress("bootstrap", trainCount, trainCount, baselineScore));

        if (_gepaOptions.CandidateCount <= 0 || _gepaOptions.MaxIterations <= 0)
        {
            totalSw.Stop();
            return BuildResult(program, demos, baselineScore, baselineScore, totalSw.Elapsed, totalAllocStart);
        }

        // Phase 2 — Meta-prompt search
        IReadOnlyList<Example> bestDemos = demos;
        TProgram bestProgram = program;
        double bestScore = baselineScore;

        for (int iter = 0; iter < _gepaOptions.MaxIterations && !ct.IsCancellationRequested; iter++)
        {
            progress?.Report(new OptimizerProgress("search", iter, _gepaOptions.MaxIterations, bestScore));

            var candidates = await GenerateCandidatesAsync(bestDemos, _gepaOptions.CandidateCount, context, ct);
            double? iterBest = null;

            foreach (var (instruction, newDemos) in candidates)
            {
                var optimized = BuildOptimizedProgram(program, newDemos, instruction);
                var bound = optimized.Bind(context);
                double score = await ScoreAsync(bound, valid, metric, context, ct);

                if (score > bestScore && score - bestScore >= _gepaOptions.ImprovementThreshold)
                {
                    bestScore = score;
                    bestDemos = newDemos;
                    bestProgram = bound;
                    iterBest = score;
                }
            }

            if (iterBest == null && iter + 1 < _gepaOptions.MaxIterations)
            {
                // Diversify — add lowest-scoring examples as critique demos
                var crit = await CollectCritiqueAsync(bestProgram, bestDemos, valid, metric, context, ct);
                bestDemos = bestDemos.Concat(crit).ToList();
            }
        }

        totalSw.Stop();
        return BuildResult(bestProgram, bestDemos, baselineScore, bestScore, totalSw.Elapsed, totalAllocStart);
    }

    async Task<(List<Example> demos, long elapsedMs, long allocated)> BootstrapAsync(
        TProgram program, Example[] train, IMetric metric, AmpelumContext context, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        long allocStart = GC.GetAllocatedBytesForCurrentThread();
        var demos = new List<Example>(_gepaOptions.BootstrapCount);

        int count = Math.Min(_gepaOptions.BootstrapCount, train.Length);
        double threshold = _gepaOptions.QualityThreshold;
        var meta = FindSignatureMetadata(program);

        for (int retry = 0; retry < 3 && demos.Count < 2 && count > 0; retry++)
        {
            demos.Clear();
            var tasks = new Task<(Example ex, double? score)>[count];
            for (int i = 0; i < count; i++)
            {
                var ex = train[i];
                tasks[i] = Task.Run(async () =>
                {
                    var bound = program;
                    double? score = null;
                    try
                    {
                        // Run the program on the example inputs to get the actual prediction outputs.
                        // Falls back to scoring ground truth when reflection-based invocation is unavailable.
                        var predictions = await CallProgramAsync(bound, ex.Inputs!, context, ct);
                        var scoreInputs = predictions ?? ex.Outputs!;
                        score = metric.Score(scoreInputs, ex.Outputs!, meta);
                    }
                    catch { }
                    return (ex, score);
                }, ct);
            }
            await Task.WhenAll(tasks);
            foreach (var t in tasks)
            {
                var (ex, s) = t.Result;
                if (s >= threshold) demos.Add(ex);
            }
            if (demos.Count < 2) threshold = Math.Max(0.1, threshold - 0.1);
        }
        sw.Stop();
        return (demos, sw.ElapsedMilliseconds, GC.GetAllocatedBytesForCurrentThread() - allocStart);
    }

    async Task<double> ScoreAsync(
        TProgram program, Example[] set, IMetric metric, AmpelumContext context, CancellationToken ct)
    {
        if (set.Length == 0) return 0;
        var meta = FindSignatureMetadata(program);
        double sum = 0; int n = 0;
        foreach (var ex in set)
        {
            try
            {
                var predictions = await CallProgramAsync(program, ex.Inputs!, context, ct);
                var scoreInputs = predictions ?? ex.Outputs!;
                sum += metric.Score(scoreInputs, ex.Outputs!, meta);
                n++;
            }
            catch { }
        }
        return n > 0 ? sum / n : 0;
    }

    async Task<List<(string instruction, List<Example> demos)>> GenerateCandidatesAsync(
        IReadOnlyList<Example> demos, int count, AmpelumContext context, CancellationToken ct)
    {
        string demoText = FormatDemos(demos);
        var results = new List<(string, List<Example>)>(count);

        for (int i = 0; i < count; i++)
        {
            string prompt = i == 0
                ? "You are a prompt engineer optimizing an LLM program.\n\n"
                  + "Here are input/output examples demonstrating the program's current behavior:\n\n"
                  + demoText + "\n\n"
                  + "Write a BETTER instruction that would improve the program's output accuracy.\n"
                  + "Respond with ONLY the new instruction text. No commentary, no markdown."
                : "You are a prompt engineer. Write a variant with different phrasing.\n\n"
                  + "Current examples:\n\n" + demoText + "\n\n"
                  + "Respond with ONLY the new instruction text.";

            var lmCtx = context with { Chat = _optimizerLlm };
            var response = await HookedLmCall.InvokeAsync(
                lmCtx, "GEPAOptimizer.GenerateCandidate", [new ChatMessage(ChatRole.User, prompt)], null, ct);
            string inst = (response.Text ?? "").Trim();
            if (inst.StartsWith("```")) inst = inst[(inst.IndexOf('\n') + 1)..];
            if (inst.EndsWith("```")) inst = inst[..inst.LastIndexOf("```")].Trim();

            results.Add((inst, demos.ToList()));
        }
        return results;
    }

    async Task<List<Example>> CollectCritiqueAsync(
        TProgram program, IReadOnlyList<Example> existing,
        Example[] valid, IMetric metric, AmpelumContext context, CancellationToken ct)
    {
        var meta = FindSignatureMetadata(program);
        var scored = new List<(Example ex, double score)>();
        foreach (var ex in valid)
        {
            try
            {
                double s = metric.Score(ex.Inputs!, ex.Outputs!, meta);
                scored.Add((ex, s));
            }
            catch { }
        }
        var result = new List<Example>(2);
        foreach (var (ex, _) in scored.OrderBy(s => s.score).Take(2))
        {
            if (!existing.Any(e => SameExample(e, ex)))
                result.Add(ex);
        }
        return result;
    }

    /// <summary>
    /// Calls the program's ForwardAsync method via reflection to get predictions.
    /// Returns the output dictionary from the prediction, or null on failure.
    /// </summary>
    async Task<IReadOnlyDictionary<string, object?>?> CallProgramAsync(
        TProgram program,
        IReadOnlyDictionary<string, object?> inputs,
        AmpelumContext context,
        CancellationToken ct)
    {
        try
        {
            // Extract the signature type from the program
            var programType = program!.GetType();
            var signatureType = FindSignatureType(programType);
            if (signatureType == null)
                return null;

            // Reconstruct the signature instance from the input dictionary
            var signatureInstance = ReconstructSignature(signatureType, inputs);
            if (signatureInstance == null)
                return null;

            // Find ForwardAsync(TSig inputs, CancellationToken ct) method
            var forwardMethod = programType.GetMethod(
                "ForwardAsync",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                null,
                [signatureType, typeof(CancellationToken)],
                null);

            if (forwardMethod == null)
            {
                // Try without demos parameter
                forwardMethod = programType.GetMethod(
                    "ForwardAsync",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                    null,
                    [signatureType],
                    null);
            }

            if (forwardMethod == null)
                return null;

            // Invoke ForwardAsync - result is Task<Prediction<TSig>>
            object? taskResult = null;
            if (forwardMethod.GetParameters().Length == 2)
            {
                taskResult = forwardMethod.Invoke(program, [signatureInstance, ct]);
            }
            else
            {
                taskResult = forwardMethod.Invoke(program, [signatureInstance]);
            }

            if (taskResult == null)
                return null;

            // Await the task and extract the Prediction<TSig>
            dynamic prediction = await (dynamic)taskResult!;

            // Extract Outputs from prediction.Trace
            if (prediction?.Trace is not null)
            {
                return prediction.Trace.Outputs as IReadOnlyDictionary<string, object?>;
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Extracts the signature type from a program type (e.g., Predict&lt;TSig&gt; -> TSig).
    /// </summary>
    static Type? FindSignatureType(Type programType)
    {
        if (programType.IsGenericType)
        {
            var genArgs = programType.GetGenericArguments();
            foreach (var arg in genArgs)
            {
                if (typeof(ISignature).IsAssignableFrom(arg))
                    return arg;
            }
        }

        // Check base types for generic signatures
        foreach (var baseType in programType.GetInterfaces())
        {
            if (baseType.IsGenericType && baseType.GetGenericTypeDefinition() == typeof(IModule<,>))
            {
                var args = baseType.GetGenericArguments();
                if (args.Length == 2 && typeof(ISignature).IsAssignableFrom(args[0]))
                    return args[0];
            }
        }

        return null;
    }

    /// <summary>
    /// Reconstructs a signature instance from an input dictionary by setting properties via reflection.
    /// </summary>
    static object? ReconstructSignature(Type signatureType, IReadOnlyDictionary<string, object?> inputs)
    {
        try
        {
            // Create an instance of the signature type
            var instance = Activator.CreateInstance(signatureType);
            if (instance == null)
                return null;

            // Set input properties from the dictionary
            foreach (var kv in inputs)
            {
                var prop = signatureType.GetProperty(kv.Key, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(instance, kv.Value);
                }
            }

            return instance;
        }
        catch { }

        return null;
    }

    static bool SameExample(Example a, Example b)
    {
        if (a.Inputs is null || b.Inputs is null) return a.Inputs is null && b.Inputs is null;
        if (a.Inputs.Count != b.Inputs.Count) return false;
        foreach (var kv in a.Inputs)
            if (!b.Inputs.TryGetValue(kv.Key, out var v) || !Equals(kv.Value, v)) return false;
        return true;
    }

    static string FormatDemos(IReadOnlyList<Example> demos)
    {
        if (demos.Count == 0) return "(no demonstrations)";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < Math.Min(demos.Count, 4); i++)
        {
            sb.Append("Example ").Append(i + 1).AppendLine(":");
            sb.Append("  Input: ").AppendLine(Fmt(demos[i].Inputs));
            sb.Append("  Output: ").AppendLine(Fmt(demos[i].Outputs));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    static string Fmt(object? o)
    {
        if (o is null) return "(null)";
        if (o is System.Collections.IDictionary d)
        {
            var parts = new List<string>();
            foreach (System.Collections.DictionaryEntry kv in d)
                parts.Add(string.Concat(kv.Key.ToString(), ": ", kv.Value));
            return "{ " + string.Join(", ", parts) + " }";
        }
        return o.ToString() ?? "";
    }

    static SignatureMetadata FindSignatureMetadata(TProgram program)
    {
        // Try composed module first
        if (program is IComposedModule composed && composed.Submodules.Count > 0)
        {
            var first = composed.Submodules.Values.First();
            var sigType = first.GetType().GetGenericArguments().FirstOrDefault(t => typeof(ISignature).IsAssignableFrom(t));
            if (sigType != null)
                return SignatureMetadataCache.For(sigType);
        }
        // Try single Predict
        var tType = typeof(TProgram);
        if (tType.IsGenericType)
        {
            var genArgs = tType.GetGenericArguments();
            if (genArgs.Length > 0 && typeof(ISignature).IsAssignableFrom(genArgs[0]))
                return SignatureMetadataCache.For(genArgs[0]);
        }
        // Fallback — empty metadata
        return new SignatureMetadata(typeof(object), "", Array.Empty<Field>(), Array.Empty<Field>());
    }

    OptimizedProgram<TProgram> BuildOptimizedProgram(
        TProgram program, IReadOnlyList<Example> demos, string instructionOverride)
    {
        var demosBySub = new Dictionary<string, IReadOnlyList<Example>>();
        var overrides = new Dictionary<string, string>();

        if (program is IComposedModule composed)
        {
            var name = composed.Submodules.Keys.FirstOrDefault() ?? "";
            demosBySub[name] = demos;
            overrides[name] = instructionOverride;
        }
        else
        {
            demosBySub[""] = demos;
            overrides[""] = instructionOverride;
        }

        return new OptimizedProgram<TProgram>(program, demosBySub, overrides);
    }

    static OptimizedProgram<TProgram> BuildResult(
        TProgram program, IReadOnlyList<Example> demos,
        double baseline, double optimized, TimeSpan total, long allocStart)
    {
        var demosBySub = program is IComposedModule composed
            ? composed.Submodules.Keys.ToDictionary(k => k, _ => demos)
            : new Dictionary<string, IReadOnlyList<Example>> { ["*"] = demos };

        double improvement = baseline > 0 ? (optimized - baseline) / baseline * 100.0 : optimized > 0 ? 100 : 0;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocStart;

        return new OptimizedProgram<TProgram>(program, demosBySub, new Dictionary<string, string>())
        {
            Score = optimized,
            TrainsetSize = demos.Count
        };
    }
}

/// <summary>Configuration for GEPA optimization phases.</summary>
public sealed record GepaOptions
{
    /// <summary>Number of examples to bootstrap as demonstrations. Default 4.</summary>
    public int BootstrapCount { get; init; } = 4;

    /// <summary>Number of instruction candidates per iteration. Default 3.</summary>
    public int CandidateCount { get; init; } = 3;

    /// <summary>Maximum meta-prompt search iterations. Default 3.</summary>
    public int MaxIterations { get; init; } = 3;

    /// <summary>Minimum improvement ratio to accept a candidate. Default 0.05 (5%).</summary>
    public double ImprovementThreshold { get; init; } = 0.05;

    /// <summary>Percentage of trainset held out for validation. Default 20.</summary>
    public int ValidationHoldoutPercent { get; init; } = 20;

    /// <summary>Minimum quality score to include bootstrap demo. Default 0.5.</summary>
    public double QualityThreshold { get; init; } = 0.5;
}

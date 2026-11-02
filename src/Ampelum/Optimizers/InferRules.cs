using System.Diagnostics;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Hooks;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Optimizers;

/// <summary>
/// Distils a symbolic rule-string from training demonstrations by prompting an LM to
/// summarise patterns across the trainset. The distilled rules are stored as
/// instruction overrides for every submodule (or under the "default" key when the
/// program has no submodules).
/// </summary>
/// <typeparam name="TProgram">The user program type.</typeparam>
public sealed class InferRules<TProgram> : IOptimizer<TProgram>
    where TProgram : class
{
    readonly IChatClient _ruleProposer;

    /// <summary>Construct an InferRules optimizer.</summary>
    public InferRules(IChatClient ruleProposer)
    {
        ArgumentNullException.ThrowIfNull(ruleProposer);
        _ruleProposer = ruleProposer;
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
        activity?.SetTag("optimizer.type", nameof(InferRules<TProgram>));
        activity?.SetTag("optimizer.trainset_size", trainset.Count);

        var sw = Stopwatch.StartNew();

        if (trainset.Count == 0)
        {
            sw.Stop();
            Diagnostics.OptimizerCompileDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("optimizer.type", nameof(InferRules<TProgram>)));
            Diagnostics.OptimizerCompileExamples.Record(0L,
                new KeyValuePair<string, object?>("optimizer.type", nameof(InferRules<TProgram>)));
            return new OptimizedProgram<TProgram>(
                Program: program,
                DemosBySubmodule: new Dictionary<string, IReadOnlyList<Example>>(),
                InstructionOverrides: new Dictionary<string, string>());
        }

        var exampleSummary = string.Join("\n",
            trainset.Take(5).Select((ex, i) =>
                $"Example {i + 1}: input={Format(ex.Inputs)} expected={Format(ex.Outputs)}"));

        var prompt =
            "Below are example input-output pairs. Infer concise rules that describe " +
            "the reasoning pattern. Return them under a '[[ ## Rules ## ]]' header.\n\n" +
            exampleSummary;

        ChatResponse response;
        try
        {
            // HookedLmCall requires an AmpelumContext, and _ruleProposer may be a distinct
            // IChatClient from context.Chat (the caller can wire a dedicated rule-proposer
            // client) — rebind context.Chat to _ruleProposer via `with` so the actual call
            // still goes to the configured proposer while running through context's hook
            // pipeline (budget/audit/PII/etc.), exactly as the direct call did before.
            var proposerCtx = context with { Chat = _ruleProposer };
            var messages = new List<ChatMessage> { new(ChatRole.User, prompt) };
#pragma warning disable AMPELUM_V09_PREVIEW
            response = await HookedLmCall.InvokeAsync(
                proposerCtx, "InferRules.ProposeRules", messages, options: null, cancellationToken: ct).ConfigureAwait(false);
#pragma warning restore AMPELUM_V09_PREVIEW
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new OptimizerException("InferRules proposer failed.", ex);
        }

        var ruleText = response.Text ?? string.Empty;
        const string marker = "[[ ## Rules ## ]]";
        var idx = ruleText.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        var rules = idx >= 0 ? ruleText[(idx + marker.Length)..].Trim() : ruleText.Trim();

        var names = program is IComposedModule cm && cm.Submodules.Count > 0
            ? cm.Submodules.Keys.ToList()
            : ["default"];

        var overrides = names.ToDictionary(n => n, _ => rules);

        progress?.Report(new OptimizerProgress(
            Phase: "rules_inferred", Step: 1, TotalSteps: 1, BestScore: 0.0));

        sw.Stop();
        Diagnostics.OptimizerCompileDuration.Record(sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("optimizer.type", nameof(InferRules<TProgram>)));
        Diagnostics.OptimizerCompileExamples.Record(trainset.Count,
            new KeyValuePair<string, object?>("optimizer.type", nameof(InferRules<TProgram>)));

        return new OptimizedProgram<TProgram>(
            Program: program,
            DemosBySubmodule: new Dictionary<string, IReadOnlyList<Example>>(),
            InstructionOverrides: overrides)
        {
            TrainsetSize = trainset.Count,
        };
    }

    static string Format(IReadOnlyDictionary<string, object?> dict) =>
        "{" + string.Join(", ", dict.Select(kv => $"{kv.Key}={kv.Value}")) + "}";
}

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Hooks;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Optimizers;

/// <summary>
/// COPRO (Coordinate Ascent Prompt Optimizer). For each submodule, uses an instruction-proposer
/// LM to generate <see cref="CandidatesPerSubmodule"/> candidate instructions, evaluates each
/// on the trainset via the metric supplied to <c>CompileAsync</c>, and keeps the highest-scoring
/// instruction as an <see cref="OptimizedProgram{TProgram}.InstructionOverrides"/> entry.
///
/// <para>
/// Evaluation mode depends on whether a <c>programRunner</c> is supplied to the constructor.
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>With programRunner (correct mode):</b> for each candidate instruction, COPRO builds a
///     temporary <see cref="OptimizedProgram{TProgram}"/> carrying the candidate as an
///     instruction override for the current submodule, binds it via
///     <see cref="OptimizedProgram{TProgram}.Bind"/>, runs the program against each trainset
///     example through <c>programRunner</c>, then scores actual-vs-expected via the metric.
///   </description></item>
///   <item><description>
///     <b>Without programRunner (degenerate mode, retained for backward compatibility):</b>
///     the candidate is not actually executed against the program. Each candidate is scored by
///     calling <c>metric.Score(ex.Outputs, ex.Outputs, â€¦)</c>, which compares the example's
///     expected output to itself. All candidates score identically and the first non-empty
///     proposal wins on tie-break. This mode is preserved so v0.5.0 callers continue to work,
///     but the optimization signal is noise; pass a programRunner for real evaluation.
///   </description></item>
/// </list>
///
/// Constructor metric parameter is intentionally absent (PSQ-3): the metric is supplied
/// per-run via <c>CompileAsync</c> to keep the optimizer reusable across metrics.
/// </summary>
/// <typeparam name="TProgram">The user program type.</typeparam>
public sealed class Copro<TProgram> : IOptimizer<TProgram>
{
    static readonly SignatureMetadata _placeholderMeta =
        new(typeof(object), "", [], []);

    readonly IChatClient _instructionProposer;
    readonly Func<TProgram, Example, AmpelumContext, CancellationToken, Task<Example>>? _programRunner;

    /// <summary>Number of candidate instructions generated per submodule per compile run.</summary>
    public int CandidatesPerSubmodule { get; }

    /// <summary>Construct a COPRO optimizer.</summary>
    /// <param name="instructionProposer">Chat client used to propose candidate instructions.</param>
    /// <param name="candidatesPerSubmodule">Candidates generated per submodule per compile run.</param>
    /// <param name="programRunner">
    /// Optional program runner. When non-null, candidates are evaluated by running the program
    /// with the candidate instruction injected as an override and scoring actual-vs-expected.
    /// When null, candidates are not really executed (see class XML-doc).
    /// </param>
    public Copro(
        IChatClient instructionProposer,
        int candidatesPerSubmodule = 5,
        Func<TProgram, Example, AmpelumContext, CancellationToken, Task<Example>>? programRunner = null)
    {
        ArgumentNullException.ThrowIfNull(instructionProposer);
        _instructionProposer = instructionProposer;
        CandidatesPerSubmodule = candidatesPerSubmodule;
        _programRunner = programRunner;
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
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(trainset);
        ArgumentNullException.ThrowIfNull(metric);
        ArgumentNullException.ThrowIfNull(context);

        using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.optimizer.compile");
        activity?.SetTag("optimizer.type", nameof(Copro<TProgram>));
        activity?.SetTag("optimizer.trainset_size", trainset.Count);

        var sw = Stopwatch.StartNew();

        var submoduleNames = (program is IComposedModule cm)
            ? cm.Submodules.Keys.ToList()
            : new List<string> { "__single__" };

        var bestInstructions = new Dictionary<string, string>();
        double overallBestScore = 0.0;

        int totalSteps = submoduleNames.Count * CandidatesPerSubmodule;
        int step = 0;

        foreach (var submoduleName in submoduleNames)
        {
            ct.ThrowIfCancellationRequested();

            double localBestScore = double.NegativeInfinity;
            string? localBestInstruction = null;

            for (int c = 0; c < CandidatesPerSubmodule; c++)
            {
                ct.ThrowIfCancellationRequested();
                step++;

                var proposalMessages = new List<ChatMessage>
                {
                    new(ChatRole.System,
                        "You are a prompt engineer. Generate a single short, improved instruction " +
                        $"for the submodule '{submoduleName}'. Respond with only the instruction text."),
                    new(ChatRole.User,
                        $"Generate instruction candidate {c + 1} of {CandidatesPerSubmodule}."),
                };

                ChatResponse proposalResponse;
                try
                {
                    var lmCtx = context with { Chat = _instructionProposer };
#pragma warning disable AMPELUM_V09_PREVIEW
                    proposalResponse = await HookedLmCall.InvokeAsync(
                        lmCtx, "CoproOptimizer.ProposeInstruction", proposalMessages, cancellationToken: ct).ConfigureAwait(false);
#pragma warning restore AMPELUM_V09_PREVIEW
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    throw new OptimizerException(
                        $"Instruction proposer failed for submodule '{submoduleName}'.", ex);
                }

                var candidate = proposalResponse.Text?.Trim() ?? "";
                if (string.IsNullOrEmpty(candidate)) continue;

                double totalScore = 0.0;
                int evaluated = 0;

                // When a programRunner is available, build a per-candidate context carrying the
                // candidate as an instruction override and bind the program through it so any
                // submodule reading its own SubmoduleName picks up the override. The runner
                // itself also receives the override-carrying context so user code that resolves
                // overrides at the program level works.
                TProgram boundForCandidate = program;
                AmpelumContext runCtx = context;
                if (_programRunner is not null)
                {
                    runCtx = context with
                    {
                        InstructionOverrides =
                            new Dictionary<string, string> { [submoduleName] = candidate },
                    };
                    var probe = new OptimizedProgram<TProgram>(
                        program,
                        new Dictionary<string, IReadOnlyList<Example>>(),
                        new Dictionary<string, string> { [submoduleName] = candidate });
                    boundForCandidate = probe.Bind(runCtx);
                }

                foreach (var ex in trainset)
                {
                    try
                    {
                        double score;
                        if (_programRunner is not null)
                        {
                            var actual = await _programRunner(boundForCandidate, ex, runCtx, ct)
                                .ConfigureAwait(false);
                            score = metric.Score(actual.Outputs, ex.Outputs, _placeholderMeta);
                        }
                        else
                        {
                            score = metric.Score(ex.Outputs, ex.Outputs, _placeholderMeta);
                        }
                        totalScore += score;
                        evaluated++;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch
                    {
                        // skip examples that throw in metric or runner
                    }
                }

                var avgScore = evaluated > 0 ? totalScore / evaluated : 0.0;

                if (avgScore > localBestScore)
                {
                    localBestScore = avgScore;
                    localBestInstruction = candidate;
                }

                if (localBestScore > overallBestScore)
                    overallBestScore = localBestScore;

                progress?.Report(new OptimizerProgress(
                    "Propose",
                    step,
                    totalSteps,
                    overallBestScore));
            }

            if (localBestInstruction is not null)
                bestInstructions[submoduleName] = localBestInstruction;
        }

        sw.Stop();
        Diagnostics.OptimizerCompileDuration.Record(sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("optimizer.type", nameof(Copro<TProgram>)));
        Diagnostics.OptimizerCompileExamples.Record(trainset.Count,
            new KeyValuePair<string, object?>("optimizer.type", nameof(Copro<TProgram>)));
        Diagnostics.OptimizerCompileScore.Record(overallBestScore,
            new KeyValuePair<string, object?>("optimizer.type", nameof(Copro<TProgram>)));

        activity?.SetTag("optimizer.final_score", overallBestScore);

        return new OptimizedProgram<TProgram>(
            program,
            new Dictionary<string, IReadOnlyList<Example>>(),
            bestInstructions)
        {
            Score         = overallBestScore,
            TrainsetSize  = trainset.Count,
        };
    }
}

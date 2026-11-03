using Ampelum.Samples.PromptTuning;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Persistence;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace Ampelum.Samples.PromptTuning.Tests;

public sealed class PromptTuningPipelineTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "prompt-tuning-tests-" + Guid.NewGuid().ToString("N"));

    public PromptTuningPipelineTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // Rewrites the instruction in the saved file right after the real save.
    sealed class EditAfterSave(string from, string to) : IProgramPersistence
    {
        readonly JsonProgramPersistence _real = new();

        public async Task SaveAsync<TProgram>(
            SignalGrid.Ampelum.Optimizers.OptimizedProgram<TProgram> optimizedProgram,
            string path,
            CancellationToken ct = default)
            where TProgram : class
        {
            await _real.SaveAsync(optimizedProgram, path, ct);
            var text = await File.ReadAllTextAsync(path, ct);
            text.ShouldContain(from);
            await File.WriteAllTextAsync(path, text.Replace(from, to), ct);
        }

        public Task<SignalGrid.Ampelum.Optimizers.OptimizedProgram<TProgram>> LoadAsync<TProgram>(
            string path,
            CancellationToken ct = default)
            where TProgram : class, new() =>
            _real.LoadAsync<TProgram>(path, ct);
    }

    string ProgramPath => Path.Combine(_dir, "program.json");

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static string SystemText(RecordedChatCall call) =>
        call.Messages.First(m => m.Role == ChatRole.System).Text;

    // A full run happens every FullEvalEvery trials and at the last trial.
    static int FullRuns =>
        PromptTuningPipeline.NumTrials / PromptTuningPipeline.FullEvalEvery
        + (PromptTuningPipeline.NumTrials % PromptTuningPipeline.FullEvalEvery == 0 ? 0 : 1);

    static int ExpectedClassifierCalls(int rows) =>
        rows                                                          // baseline
        + rows * PromptTuningPipeline.NumDemoSets                     // bootstrap, one pass per demo set
        + PromptTuningPipeline.NumTrials * PromptTuningPipeline.MinibatchSize
        + FullRuns * rows                                             // full runs
        + rows;                                                       // reloaded program

    PromptTuningPipeline NewPipeline(
        IChatClient client,
        IChatClient proposer,
        string? datasetPath = null,
        IProgramPersistence? persistence = null) =>
        new(client, proposer, datasetPath ?? SentimentData.DefaultPath, ProgramPath,
            persistence ?? new JsonProgramPersistence(), new LabelMatch());

    async Task<(ScriptedChatClient Client, ScriptedProposerClient Proposer, PromptTuningResult Result, int Rows)>
        RunScriptedAsync()
    {
        var data = await SentimentData.LoadAsync(SentimentData.DefaultPath, Ct);
        var client = ScriptedChatClient.Create(data);
        var proposer = ScriptedProposerClient.Create();
        var result = await NewPipeline(client, proposer).RunAsync(Ct);
        return (client, proposer, result, data.Count);
    }

    [Fact]
    public async Task Scripted_replies_give_lower_baseline_and_higher_tuned_score()
    {
        var (_, _, result, rows) = await RunScriptedAsync();

        rows.ShouldBe(8);
        result.Baseline.Mean.ShouldBe(0.375);
        result.Tuned.Mean.ShouldBe(1.0);
        result.Baseline.FailureCount.ShouldBe(0);
        result.Tuned.FailureCount.ShouldBe(0);
        result.ReportMarkdown.ShouldContain("tuned wins");
    }

    [Fact]
    public async Task Saved_program_file_exists_and_carries_the_tuned_instruction_and_demos()
    {
        var (_, _, result, _) = await RunScriptedAsync();

        File.Exists(ProgramPath).ShouldBeTrue();
        result.TunedInstruction.ShouldBe(ScriptedChatClient.TunedInstruction);
        result.TunedInstruction.ShouldNotBe(result.BaselineInstruction);

        var loaded = await new JsonProgramPersistence().LoadAsync<SentimentProgram>(ProgramPath, Ct);
        loaded.InstructionOverrides[SentimentProgram.ClassifierName].ShouldBe(ScriptedChatClient.TunedInstruction);
        var demos = loaded.DemosBySubmodule[SentimentProgram.ClassifierName];
        demos.Count.ShouldBe(PromptTuningPipeline.BootstrappedDemos);
        result.TunedDemoCount.ShouldBe(demos.Count);
    }

    [Fact]
    public async Task The_seed_fixes_the_tips_and_the_demos_the_optimizer_uses()
    {
        var (_, proposer, result, _) = await RunScriptedAsync();

        var loaded = await new JsonProgramPersistence().LoadAsync<SentimentProgram>(ProgramPath, Ct);
        var feedback = loaded.DemosBySubmodule[SentimentProgram.ClassifierName]
            .Select(d => Convert.ToString(d.Inputs[nameof(SentimentSig.Feedback)])).ToList();
        var tips = proposer.RecordedCalls.Skip(1)
            .Select(c => c.Messages.Last().Text.Split('\n').Single(l => l.StartsWith("Tip: ", StringComparison.Ordinal)))
            .ToList();

        result.TunedDemoCount.ShouldBe(2);
        feedback.ShouldBe(ExpectedDemoFeedback);
        tips.ShouldBe(ExpectedTips);
    }

    // What seed 17 picks with this dataset and the scripted models. Both rows are among the first three,
    // the only ones the untuned classifier answers right: a demo has to pass the metric.
    static readonly string[] ExpectedDemoFeedback =
    [
        "The checkout was quick and the support team solved my issue in minutes.",
        "The package came in a standard box.",
    ];

    [Fact]
    public async Task Demos_come_only_from_rows_the_untuned_classifier_answered_right()
    {
        var data = await SentimentData.LoadAsync(SentimentData.DefaultPath, Ct);
        var answeredRight = data.Take(ScriptedChatClient.BaselineCorrect).Select(e => e.Inputs.Feedback).ToList();
        await RunScriptedAsync();

        var loaded = await new JsonProgramPersistence().LoadAsync<SentimentProgram>(ProgramPath, Ct);
        var demos = loaded.DemosBySubmodule[SentimentProgram.ClassifierName];

        demos.ShouldNotBeEmpty();
        demos.ShouldAllBe(d => answeredRight.Contains(Convert.ToString(d.Inputs[nameof(SentimentSig.Feedback)])!));
        demos.ShouldAllBe(d =>
            data.Single(e => e.Inputs.Feedback == Convert.ToString(d.Inputs[nameof(SentimentSig.Feedback)]))
                .Expected.Sentiment == Convert.ToString(d.Outputs[nameof(SentimentSig.Sentiment)]));
    }

    static readonly string[] ExpectedTips =
    [
        "Tip: Tell the model what to avoid.",
        "Tip: Tell the model what to avoid.",
    ];

    [Fact]
    public async Task The_same_seed_gives_the_same_run()
    {
        var (clientA, proposerA, resultA, _) = await RunScriptedAsync();
        var (clientB, proposerB, resultB, _) = await RunScriptedAsync();

        resultB.TunedInstruction.ShouldBe(resultA.TunedInstruction);
        resultB.TunedDemoCount.ShouldBe(resultA.TunedDemoCount);
        resultB.Tuned.Mean.ShouldBe(resultA.Tuned.Mean);
        string[] Texts(IReadOnlyList<RecordedChatCall> calls) =>
            [.. calls.Select(c => string.Join('\n', c.Messages.Select(m => m.Role + ":" + m.Text)))];
        Texts(clientB.RecordedCalls).ShouldBe(Texts(clientA.RecordedCalls));
        Texts(proposerB.RecordedCalls).ShouldBe(Texts(proposerA.RecordedCalls));
    }

    [Fact]
    public async Task The_proposer_gets_a_dataset_summary_request_then_grounded_proposal_requests()
    {
        var (_, proposer, _, _) = await RunScriptedAsync();

        var calls = proposer.RecordedCalls;
        calls.Count.ShouldBe(1 + PromptTuningPipeline.InstructionCandidates);
        calls[0].Messages.Last().Text.ShouldContain("The checkout was quick");
        foreach (var proposal in calls.Skip(1))
        {
            var prompt = proposal.Messages.Last().Text;
            prompt.ShouldContain("Dataset summary: " + ScriptedProposerClient.Summary);
            prompt.ShouldContain("Module: " + SentimentProgram.ClassifierName);
            prompt.ShouldContain("Tip: ");
        }
    }

    [Fact]
    public async Task Tuned_instruction_and_demos_reach_the_model_only_after_the_tuned_program_is_bound()
    {
        var (client, _, result, rows) = await RunScriptedAsync();

        var calls = client.RecordedCalls;
        calls.Count.ShouldBe(ExpectedClassifierCalls(rows));

        // The untuned baseline: signature instruction, no demos.
        var baseline = calls.Take(rows).ToList();
        baseline.ShouldAllBe(c => SystemText(c).Contains(result.BaselineInstruction));
        baseline.ShouldAllBe(c => !SystemText(c).Contains(ScriptedChatClient.TunedInstruction));
        baseline.ShouldAllBe(c => c.Messages.Count == 2);

        // The reloaded program: tuned instruction and the saved demos.
        var reloaded = calls.Skip(calls.Count - rows).ToList();
        reloaded.ShouldAllBe(c => SystemText(c).Contains(ScriptedChatClient.TunedInstruction));
        reloaded.ShouldAllBe(c => c.Messages.Count == 2 + 2 * result.TunedDemoCount);

        // Bind the saved program by hand to a fresh client and check one real call.
        var loaded = await new JsonProgramPersistence().LoadAsync<SentimentProgram>(ProgramPath, Ct);
        var fresh = new FakeChatClient().EnqueueOutputs(new SentimentSig { Sentiment = "positive" });
        var context = new AmpelumContext(fresh, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        var bound = loaded.Bind(context);
        await bound.ClassifyAsync(new SentimentSig { Feedback = "anything" }, Ct);

        SystemText(fresh.RecordedCalls.Single()).ShouldContain(ScriptedChatClient.TunedInstruction);
        fresh.RecordedCalls.Single().Messages.Count.ShouldBe(2 + 2 * result.TunedDemoCount);
    }

    [Fact]
    public async Task Tuned_score_comes_from_the_program_reloaded_from_the_file()
    {
        const string Edited = "Reply with the label only.";
        var data = await SentimentData.LoadAsync(SentimentData.DefaultPath, Ct);
        var client = ScriptedChatClient.Create(data);
        var persistence = new EditAfterSave(ScriptedChatClient.TunedInstruction, Edited);

        var result = await NewPipeline(client, ScriptedProposerClient.Create(), persistence: persistence).RunAsync(Ct);

        // The file was edited after the save, so only a real reload can see the edited text.
        result.TunedInstruction.ShouldBe(Edited);
        var tunedRun = client.RecordedCalls.Skip(client.RecordedCalls.Count - data.Count).ToList();
        tunedRun.Count.ShouldBe(data.Count);
        tunedRun.ShouldAllBe(c => SystemText(c).Contains(Edited));
        tunedRun.ShouldAllBe(c => !SystemText(c).Contains(ScriptedChatClient.TunedInstruction));
        // The script only rewards its own instruction, so the edited text scores like the baseline,
        // plus the one extra row the saved demos earn.
        result.Tuned.Mean.ShouldBe((ScriptedChatClient.BaselineCorrect + ScriptedChatClient.DemoBonus) / 8.0);
    }

    [Fact]
    public async Task Candidate_scoring_below_the_baseline_is_not_adopted()
    {
        const string Worse = "Answer in one word.";
        var data = await SentimentData.LoadAsync(SentimentData.DefaultPath, Ct);
        var client = ScriptedChatClient.Create(data, rewarded: Worse, rewardedCorrect: 1);
        var proposer = ScriptedProposerClient.Create(Worse);

        var result = await NewPipeline(client, proposer).RunAsync(Ct);

        var loaded = await new JsonProgramPersistence().LoadAsync<SentimentProgram>(ProgramPath, Ct);
        loaded.InstructionOverrides.ShouldNotContainKey(SentimentProgram.ClassifierName);
        result.TunedInstruction.ShouldBe(result.BaselineInstruction);
        result.Baseline.Mean.ShouldBe(0.375);
        result.Tuned.Mean.ShouldBeGreaterThanOrEqualTo(result.Baseline.Mean);
        var tunedRun = client.RecordedCalls.Skip(client.RecordedCalls.Count - data.Count).ToList();
        tunedRun.ShouldAllBe(c => SystemText(c).Contains(result.BaselineInstruction));
        tunedRun.ShouldAllBe(c => !SystemText(c).Contains(Worse));
    }

    [Fact]
    public async Task Missing_dataset_file_fails_with_a_clear_error_before_any_model_call()
    {
        var missing = Path.Combine(_dir, "nope.jsonl");
        var client = new FakeChatClient();
        var proposer = new FakeChatClient();

        var error = await Should.ThrowAsync<FileNotFoundException>(
            () => NewPipeline(client, proposer, missing).RunAsync(Ct));

        error.Message.ShouldContain("Dataset file not found");
        error.Message.ShouldContain(missing);
        client.RecordedCalls.ShouldBeEmpty();
        proposer.RecordedCalls.ShouldBeEmpty();
        File.Exists(ProgramPath).ShouldBeFalse();
    }
}

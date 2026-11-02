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

    PromptTuningPipeline NewPipeline(
        IChatClient client, string? datasetPath = null, IProgramPersistence? persistence = null) =>
        new(client, datasetPath ?? SentimentData.DefaultPath, ProgramPath,
            persistence ?? new JsonProgramPersistence(), new LabelMatch());

    async Task<(ScriptedChatClient Client, PromptTuningResult Result, int Rows)> RunScriptedAsync()
    {
        var data = await SentimentData.LoadAsync(SentimentData.DefaultPath, Ct);
        var client = ScriptedChatClient.Create(data);
        var result = await NewPipeline(client).RunAsync(Ct);
        return (client, result, data.Count);
    }

    [Fact]
    public async Task Scripted_replies_give_lower_baseline_and_higher_tuned_score()
    {
        var (_, result, rows) = await RunScriptedAsync();

        rows.ShouldBe(8);
        result.Baseline.Mean.ShouldBe(0.375);
        result.Tuned.Mean.ShouldBe(0.875);
        result.Baseline.FailureCount.ShouldBe(0);
        result.Tuned.FailureCount.ShouldBe(0);
        result.ReportMarkdown.ShouldContain("tuned wins");
    }

    [Fact]
    public async Task Saved_program_file_exists_and_carries_the_tuned_instruction()
    {
        var (_, result, _) = await RunScriptedAsync();

        File.Exists(ProgramPath).ShouldBeTrue();
        result.TunedInstruction.ShouldBe(ScriptedChatClient.TunedInstruction);
        result.TunedInstruction.ShouldNotBe(result.BaselineInstruction);

        var loaded = await new JsonProgramPersistence().LoadAsync<SentimentProgram>(ProgramPath, Ct);
        loaded.InstructionOverrides[SentimentProgram.ClassifierName].ShouldBe(ScriptedChatClient.TunedInstruction);
    }

    [Fact]
    public async Task Tuned_instruction_reaches_the_model_only_after_the_tuned_program_is_bound()
    {
        var (client, result, rows) = await RunScriptedAsync();

        // baseline, bootstrap, score, one proposal, candidate, score, tuned
        var calls = client.RecordedCalls;
        calls.Count.ShouldBe(6 * rows + 1);

        calls.Take(rows).ShouldAllBe(c => SystemText(c).Contains(result.BaselineInstruction));
        calls.Take(rows).ShouldAllBe(c => !SystemText(c).Contains(ScriptedChatClient.TunedInstruction));
        calls.Skip(5 * rows + 1).ShouldAllBe(c => SystemText(c).Contains(ScriptedChatClient.TunedInstruction));

        // Bind the saved program by hand to a fresh client and check one real call.
        var loaded = await new JsonProgramPersistence().LoadAsync<SentimentProgram>(ProgramPath, Ct);
        var fresh = new FakeChatClient().EnqueueOutputs(new SentimentSig { Sentiment = "positive" });
        var context = new AmpelumContext(fresh, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        var bound = loaded.Bind(context);
        await bound.ClassifyAsync(new SentimentSig { Feedback = "anything" }, Ct);

        SystemText(fresh.RecordedCalls.Single()).ShouldContain(ScriptedChatClient.TunedInstruction);
    }

    [Fact]
    public async Task Tuned_score_comes_from_the_program_reloaded_from_the_file()
    {
        const string Edited = "Reply with the label only.";
        var data = await SentimentData.LoadAsync(SentimentData.DefaultPath, Ct);
        var client = ScriptedChatClient.Create(data);
        var persistence = new EditAfterSave(ScriptedChatClient.TunedInstruction, Edited);

        var result = await NewPipeline(client, persistence: persistence).RunAsync(Ct);

        // The file was edited after the save, so only a real reload can see the edited text.
        result.TunedInstruction.ShouldBe(Edited);
        var tunedRun = client.RecordedCalls.Skip(5 * data.Count + 1).ToList();
        tunedRun.Count.ShouldBe(data.Count);
        tunedRun.ShouldAllBe(c => SystemText(c).Contains(Edited));
        tunedRun.ShouldAllBe(c => !SystemText(c).Contains(ScriptedChatClient.TunedInstruction));
        // The script only rewards its own proposal, so the edited text scores like the baseline.
        result.Tuned.Mean.ShouldBe(0.375);
    }

    [Fact]
    public async Task Candidate_scoring_below_the_baseline_is_not_adopted()
    {
        const string Worse = "Answer in one word.";
        var data = await SentimentData.LoadAsync(SentimentData.DefaultPath, Ct);
        var client = ScriptedChatClient.Create(data, proposal: Worse, proposalCorrect: 1);

        var result = await NewPipeline(client).RunAsync(Ct);

        var loaded = await new JsonProgramPersistence().LoadAsync<SentimentProgram>(ProgramPath, Ct);
        loaded.InstructionOverrides.ShouldNotContainKey(SentimentProgram.ClassifierName);
        result.TunedInstruction.ShouldBe(result.BaselineInstruction);
        result.Baseline.Mean.ShouldBe(0.375);
        result.Tuned.Mean.ShouldBe(0.375);
        var tunedRun = client.RecordedCalls.Skip(5 * data.Count + 1).ToList();
        tunedRun.ShouldAllBe(c => SystemText(c).Contains(result.BaselineInstruction));
        tunedRun.ShouldAllBe(c => !SystemText(c).Contains(Worse));
    }

    [Fact]
    public async Task Missing_dataset_file_fails_with_a_clear_error_before_any_model_call()
    {
        var missing = Path.Combine(_dir, "nope.jsonl");
        var client = new FakeChatClient();

        var error = await Should.ThrowAsync<FileNotFoundException>(
            () => NewPipeline(client, missing).RunAsync(Ct));

        error.Message.ShouldContain("Dataset file not found");
        error.Message.ShouldContain(missing);
        client.RecordedCalls.ShouldBeEmpty();
        File.Exists(ProgramPath).ShouldBeFalse();
    }
}

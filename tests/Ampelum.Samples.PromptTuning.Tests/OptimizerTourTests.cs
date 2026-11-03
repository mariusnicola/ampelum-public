using Ampelum.Samples.PromptTuning;
using Microsoft.Extensions.AI;
using Shouldly;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace Ampelum.Samples.PromptTuning.Tests;

public sealed class OptimizerTourTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static async Task<IReadOnlyList<LabeledExample<SentimentSig>>> LoadAsync() =>
        await SentimentData.LoadAsync(SentimentData.DefaultPath, Ct);

    static string SystemText(RecordedChatCall call) =>
        call.Messages.First(m => m.Role == ChatRole.System).Text;

    // ---- SIMBA ----------------------------------------------------------------------------------

    [Fact]
    public async Task SIMBA_adds_a_rule_that_raises_the_score_and_the_classifier_is_sent_it()
    {
        var data = await LoadAsync();
        var classifier = WobblingChatClient.Create(data);
        var reflection = new ScriptedRuleClient();

        var outcome = await new OptimizerTour(data, new LabelMatch()).RunSimbaAsync(classifier, reflection, Ct);

        // The classifier gets every row wrong the first time it sees it.
        outcome.Baseline.ShouldBe(0.0);
        outcome.Tuned.ShouldBe(1.0);
        // Seed 17 picks the rule strategy for the candidates that win here.
        outcome.TunedInstruction.ShouldBe($"{outcome.BaselineInstruction}\n{ScriptedChatClient.TunedInstruction}");

        // The reflection model was asked about a row: module, the row's inputs, the best and the worst run.
        var prompts = reflection.RecordedCalls.Select(c => c.Messages.Last().Text).ToList();
        prompts.ShouldNotBeEmpty();
        prompts.ShouldAllBe(p => p.Contains("Module: classify") && p.Contains("Best run, score 1") && p.Contains("Worst run, score 0"));
        prompts.ShouldAllBe(p => data.Any(row => p.Contains($"Inputs: Feedback={row.Inputs.Feedback}")));

        // The last eight classifier calls are the tuned program measured on the dataset: the rule is in the prompt.
        var measured = classifier.RecordedCalls.Skip(classifier.RecordedCalls.Count - data.Count).ToList();
        measured.ShouldAllBe(c => SystemText(c).Contains(ScriptedChatClient.TunedInstruction));
        classifier.RecordedCalls.Take(data.Count).ShouldAllBe(c => !SystemText(c).Contains(ScriptedChatClient.TunedInstruction));
    }

    [Fact]
    public async Task SIMBA_runs_are_repeatable()
    {
        var data = await LoadAsync();

        async Task<(SimbaOutcome, string[], string[])> Once()
        {
            var classifier = WobblingChatClient.Create(data);
            var reflection = new ScriptedRuleClient();
            var outcome = await new OptimizerTour(data, new LabelMatch()).RunSimbaAsync(classifier, reflection, Ct);
            return (outcome,
                [.. classifier.RecordedCalls.Select(c => string.Join('\n', c.Messages.Select(m => m.Role + ":" + m.Text)))],
                [.. reflection.RecordedCalls.Select(c => c.Messages.Last().Text)]);
        }

        var (outcomeA, callsA, reflectA) = await Once();
        var (outcomeB, callsB, reflectB) = await Once();

        outcomeB.ShouldBe(outcomeA);
        callsB.ShouldBe(callsA);
        reflectB.ShouldBe(reflectA);
    }

    // ---- BootstrapFinetune ------------------------------------------------------------------------

    [Fact]
    public async Task BootstrapFinetune_sends_the_traces_that_passed_and_the_tuned_client_is_used()
    {
        var data = await LoadAsync();
        var baseClient = ScriptedChatClient.Create(data);
        var provider = new ScriptedFineTuneProvider(data);

        var outcome = await new OptimizerTour(data, new LabelMatch()).RunBootstrapFinetuneAsync(baseClient, provider, Ct);

        // The classifier answers the first three rows right; those are the traces that pass the metric.
        var request = provider.Requests.Single();
        request.Submodule.ShouldBe(SentimentProgram.ClassifierName);
        request.Examples.Count.ShouldBe(ScriptedChatClient.BaselineCorrect);
        request.Examples.ShouldAllBe(e => e.Select(m => m.Role).SequenceEqual(new[] { ChatRole.System, ChatRole.User, ChatRole.Assistant }));
        var kept = data.Take(ScriptedChatClient.BaselineCorrect).ToList();
        for (var i = 0; i < kept.Count; i++)
        {
            var example = request.Examples[i];
            example[1].Text.ShouldContain(kept[i].Inputs.Feedback);
            example[2].Text.ShouldContain(kept[i].Expected.Sentiment);
            example[0].Text.ShouldContain(SignatureMetadataCache.For<SentimentSig>().Instructions);
        }
        data.Skip(ScriptedChatClient.BaselineCorrect).ShouldAllBe(
            row => request.Examples.All(e => !e[1].Text.Contains(row.Inputs.Feedback)));

        // The tuned client answers seven rows right; it is the client the tuned program calls.
        outcome.Baseline.ShouldBe(ScriptedChatClient.BaselineCorrect / (double)data.Count);
        outcome.Tuned.ShouldBe(ScriptedFineTuneProvider.TunedCorrect / (double)data.Count);
        var tunedClient = provider.Clients.Single();
        outcome.Program.ChatClients[SentimentProgram.ClassifierName].ShouldBeSameAs(tunedClient);
        tunedClient.RecordedCalls.Count.ShouldBe(data.Count);
        // The base client saw the baseline measurement and the teacher run, and nothing after the tuned client was bound.
        baseClient.RecordedCalls.Count.ShouldBe(2 * data.Count);
    }

    // ---- BetterTogether ---------------------------------------------------------------------------

    [Fact]
    public async Task BetterTogether_runs_prompt_weight_prompt_in_order_and_returns_the_best()
    {
        var data = await LoadAsync();
        var provider = new ScriptedFineTuneProvider(data);

        var outcome = await new OptimizerTour(data, new LabelMatch())
            .RunBetterTogetherAsync(ScriptedChatClient.Create(data), provider, Ct);

        outcome.Phases.ShouldBe(["prompt", "weight", "prompt"]);
        outcome.StepScores.Count.ShouldBe(3);
        // Demos lift the classifier by one row; the tuned client lifts it to all rows.
        outcome.StepScores[0].ShouldBe((ScriptedChatClient.BaselineCorrect + ScriptedChatClient.DemoBonus) / (double)data.Count);
        outcome.StepScores[1].ShouldBe(1.0);
        outcome.Score.ShouldBe(outcome.StepScores.Max());
        outcome.Score.ShouldBeGreaterThan(outcome.Baseline);
        outcome.Program.StepScores.ShouldBe(outcome.StepScores);

        // The weight step ran once, on the program the first step produced: with demos, four rows pass.
        provider.Requests.Count.ShouldBe(1);
        provider.Requests[0].Examples.Count.ShouldBe(ScriptedChatClient.BaselineCorrect + ScriptedChatClient.DemoBonus);
        outcome.Program.ChatClients[SentimentProgram.ClassifierName].ShouldBeSameAs(provider.Clients.Single());
        outcome.Program.DemosBySubmodule[SentimentProgram.ClassifierName].ShouldNotBeEmpty();
    }
}

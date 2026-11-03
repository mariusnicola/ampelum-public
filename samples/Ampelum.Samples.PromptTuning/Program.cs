using Ampelum.Samples.PromptTuning;
using Ampelum.Samples.Shared;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Persistence;

var useFake = args.Contains("--fake");
var datasetPath = SentimentData.DefaultPath;
var programPath = Path.Combine(Path.GetTempPath(), $"prompt-tuning-{Guid.NewGuid():N}.json");

try
{
    IChatClient chat;
    IChatClient proposer;
    if (useFake)
    {
        Console.WriteLine("[WARNING] --fake: scores come from scripted replies and are not real measurements.");
        chat = ScriptedChatClient.Create(await SentimentData.LoadAsync(datasetPath));
        proposer = ScriptedProposerClient.Create();
    }
    else
    {
        chat = SampleConfig.Load().CreateChatClient();
        proposer = chat;
    }

    using (chat)
    using (ReferenceEquals(proposer, chat) ? null : proposer)
    {
        var pipeline = new PromptTuningPipeline(
            chat, proposer, datasetPath, programPath, new JsonProgramPersistence(), new LabelMatch());
        var result = await pipeline.RunAsync();

        Console.WriteLine($"Optimizer seed:       {PromptTuningPipeline.OptimizerSeed}");
        Console.WriteLine($"Baseline instruction: {result.BaselineInstruction}");
        Console.WriteLine($"Tuned instruction:    {result.TunedInstruction}");
        Console.WriteLine($"Tuned demos:          {result.TunedDemoCount}");
        Console.WriteLine();
        Console.WriteLine(result.ReportMarkdown);
        if (useFake)
            Console.WriteLine("[WARNING] --fake: the scores above are scripted, not measured.");
        else
            Console.WriteLine("[INFO] SIMBA, BootstrapFinetune and BetterTogether run only under --fake: the library ships no fine-tuning provider.");
    }

    if (useFake)
        return await RunTourAsync(datasetPath);
    return 0;
}
catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or InvalidOperationException)
{
    Console.Error.WriteLine($"[ERROR] {ex.Message}");
    return 1;
}
finally
{
    File.Delete(programPath);
}

// SIMBA, BootstrapFinetune and BetterTogether with scripted clients and a scripted fine-tuning provider.
static async Task<int> RunTourAsync(string datasetPath)
{
    var data = await SentimentData.LoadAsync(datasetPath);
    var tour = new OptimizerTour(data, new LabelMatch());
    var failures = new List<string>();

    Console.WriteLine();
    Console.WriteLine("[WARNING] --fake: the optimizer tour below uses scripted clients and a scripted provider.");

    var simba = await tour.RunSimbaAsync(WobblingChatClient.Create(data), new ScriptedRuleClient());
    Console.WriteLine($"SIMBA                 score {simba.Baseline:0.###} -> {simba.Tuned:0.###}, " +
                      $"instruction changed: {simba.TunedInstruction != simba.BaselineInstruction}, demos: {simba.TunedDemoCount}");
    if (!(simba.Tuned > simba.Baseline)) failures.Add("SIMBA did not raise the score");

    var finetuneProvider = new ScriptedFineTuneProvider(data);
    var finetune = await tour.RunBootstrapFinetuneAsync(ScriptedChatClient.Create(data), finetuneProvider);
    Console.WriteLine($"BootstrapFinetune     score {finetune.Baseline:0.###} -> {finetune.Tuned:0.###}, " +
                      $"training examples sent: {finetuneProvider.Requests.Sum(r => r.Examples.Count)}");
    if (!(finetune.Tuned > finetune.Baseline)) failures.Add("BootstrapFinetune did not raise the score");

    var togetherProvider = new ScriptedFineTuneProvider(data);
    var together = await tour.RunBetterTogetherAsync(ScriptedChatClient.Create(data), togetherProvider);
    Console.WriteLine($"BetterTogether        score {together.Baseline:0.###} -> {together.Score:0.###}, " +
                      $"steps {string.Join(" -> ", together.Phases)}, " +
                      $"step scores {string.Join(", ", together.StepScores.Select(s => s.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)))}");
    if (!(together.Score > together.Baseline)) failures.Add("BetterTogether did not raise the score");

    foreach (var failure in failures)
        Console.Error.WriteLine($"[ERROR] {failure}");
    return failures.Count == 0 ? 0 : 1;
}

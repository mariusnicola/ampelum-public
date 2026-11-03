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
    }
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

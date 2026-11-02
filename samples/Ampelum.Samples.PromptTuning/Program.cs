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
    if (useFake)
    {
        Console.WriteLine("[WARNING] --fake: scores come from scripted replies and are not real measurements.");
        chat = ScriptedChatClient.Create(await SentimentData.LoadAsync(datasetPath));
    }
    else
    {
        chat = SampleConfig.Load().CreateChatClient();
    }

    using (chat)
    {
        var pipeline = new PromptTuningPipeline(
            chat, datasetPath, programPath, new JsonProgramPersistence(), new LabelMatch());
        var result = await pipeline.RunAsync();

        Console.WriteLine($"Baseline instruction: {result.BaselineInstruction}");
        Console.WriteLine($"Tuned instruction:    {result.TunedInstruction}");
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

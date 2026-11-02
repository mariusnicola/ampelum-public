using Ampelum.Samples.Shared;
using Ampelum.Samples.TracedPipeline;
using Microsoft.Extensions.AI;

var useFake = args.Contains("--fake");
var datasetPath = SentimentData.DefaultPath;

try
{
    IChatClient chat;
    TracingSettings tracing;
    RecordingHandler? handler = null;
    if (useFake)
    {
        Console.WriteLine("[WARNING] --fake: scores come from scripted replies and are not real measurements.");
        chat = ScriptedChatClient.Create(await SentimentData.LoadAsync(datasetPath));
        tracing = ScriptedSettings.Create();
        handler = new RecordingHandler();
    }
    else
    {
        var config = SampleConfig.Load();
        chat = config.CreateChatClient();
        tracing = TracingSettings.Load(config);
    }

    using (chat)
    {
        var pipeline = new TracedPipeline(chat, datasetPath, tracing, handler, new LabelMatch());
        var result = await pipeline.RunAsync();

        Console.WriteLine(result.ComparisonMarkdown);
        Console.WriteLine($"Traces exported to LangFuse, LangSmith and Phoenix: {result.TracesExported}");
        if (handler is not null)
        {
            foreach (var group in handler.Requests.GroupBy(r => r.Uri.Host))
                Console.WriteLine($"[FAKE HTTP] {group.Key}: {group.Count()} request(s), nothing left this machine");
            Console.WriteLine("[WARNING] --fake: the scores above are scripted, not measured.");
        }
    }
    return 0;
}
catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or InvalidOperationException)
{
    Console.Error.WriteLine($"[ERROR] {ex.Message}");
    return 1;
}

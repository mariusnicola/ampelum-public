using Ampelum.Samples.AgentService;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Ampelum.Samples.Shared;

const string Question = "How long does standard delivery take?";
const string OrderQuestion = "Where is order A-1001?";

var fake = args.Contains("--fake");
var config = fake ? null : SampleConfig.Load();
// Fake mode gets one scripted model per part; live mode shares one real client.
IChatClient Model(Func<IChatClient> scripted) => fake ? scripted() : config!.CreateChatClient();
var hostKey = fake ? "fake-host-key" : config!.Require("OPENROUTER_API_KEY");

var workDir = Path.Combine(Path.GetTempPath(), "ampelum-agent-service-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workDir);
try
{
    // 1. Serve one program over HTTP on a loopback port the OS picks.
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    await using var app = AgentServiceHost.Build(builder, Model(() => FakeScript.ServiceModel()), hostKey, workDir);
    await app.StartAsync();

    using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
    var client = new AgentServiceClient(http);

    var modules = await client.ListModulesAsync();
    Console.WriteLine($"Serving: {string.Join(", ", modules.Modules.Select(m => m.Name))} at {http.BaseAddress}");

    var answer = await client.ForwardAsync(AgentServiceHost.ModuleName, Question);
    var answerText = answer.Outputs["Answer"].GetString() ?? "";
    Console.WriteLine($"Forward: {answerText}");

    var audit = await client.ListAuditAsync();
    var verify = await client.VerifyAuditAsync();
    Console.WriteLine($"Audit:   {audit.Entries.Count} entries, chain ok = {verify.Ok}");

    // The stream endpoint sends the answer as partial frames, then one final frame with the outputs, then done.
    var frames = await client.StreamAsync(AgentServiceHost.ModuleName, Question);
    foreach (var frame in frames)
    {
        var line = frame.Event switch
        {
            "partial" => $"{frame.Data.GetProperty("field").GetString()} = {frame.Data.GetProperty("value").GetString()?.Trim()}",
            "final" => frame.Data.GetProperty("outputs").GetProperty("Answer").GetString(),
            _ => "",
        };
        Console.WriteLine($"Stream:  {frame.Event} {line}".TrimEnd());
    }
    var streamOk = frames.Count >= 2
        && frames[^2].Event == "final"
        && frames[^1].Event == "done"
        && frames.All(f => f.Event != "error");
    await app.StopAsync();

    // 2. A ReAct agent whose tool comes from an MCP tool source.
    using var tools = new ToolAgent(Model(() => FakeScript.ToolModel("A-1001")), new OrderToolSource());
    Console.WriteLine($"Tools:   {string.Join(", ", await tools.ToolNamesAsync())}");
    Console.WriteLine($"Agent:   {await tools.AskAsync(OrderQuestion)}");
    foreach (var survey in await ToolAgent.SurveyAsync(new OrderToolTransport()))
        Console.WriteLine($"Survey:  {survey.Name} declared Safe, governed as {survey.DangerTier}, confirmation required = {survey.RequiresConfirmation}");

    // 3. The REPL library, driven by a script instead of a keyboard.
    var (replOut, replErr) = await ReplSession.RunAsync(Model(() => FakeScript.ReplModel()), ReplSession.AskScript);
    Console.WriteLine($"REPL:    {ReplSession.LastValue(replOut)}");

    // 4. The CLI scores the service's answer against a reference. It runs as a process and calls no model.
    // The reference is the scripted answer, so it only means something in fake mode.
    var cliExit = 0;
    if (fake)
    {
        var dataset = Path.Combine(workDir, "dataset.jsonl");
        await CliEval.WriteDatasetAsync(dataset, [new EvalRow(Question, FakeScript.ServiceAnswer, answerText)]);
        var cli = await CliEval.RunAsync(dataset, "f1", Path.Combine(workDir, "report"));
        cliExit = cli.ExitCode;
        Console.WriteLine($"CLI:     {cli.Output.Trim()}");
    }
    else
    {
        Console.WriteLine("CLI:     skipped, no reference answer exists for a live model");
    }

    return verify.Ok && streamOk && replErr.Length == 0 && cliExit == 0 ? 0 : 1;
}
finally
{
    try
    {
        Directory.Delete(workDir, recursive: true);
    }
    catch (IOException)
    {
        // The Host's SQLite file can stay locked briefly; the directory is in the temp folder.
    }
}

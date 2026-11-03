using System.Text.Json;
using Ampelum.Samples.AgentService;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Ampelum.Samples.Shared;
using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Host.Contracts;
using SignalGrid.Ampelum.Testing;

// The stdio MCP part starts this program again as its server.
if (args.Contains(OrderMcpStdio.Flag))
    return await OrderMcpStdio.RunAsync(CancellationToken.None);

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
    string[] trainingQuestions = ["Where is my parcel?", "When does it arrive?"];
    var serviceModel = fake ? FakeScript.ServiceModel(optimizeExamples: trainingQuestions.Length) : null;
    await using var app = AgentServiceHost.Build(
        builder, serviceModel ?? config!.CreateChatClient(), hostKey, workDir);
    await app.StartAsync();

    using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
    var client = new AgentServiceClient(http);

    var modules = await client.ListModulesAsync();
    Console.WriteLine($"Serving: {string.Join(", ", modules.Modules.Select(m => m.Name))} at {http.BaseAddress}");

    var healthOk = await client.HealthStatusAsync() == System.Net.HttpStatusCode.OK;
    Console.WriteLine($"Health:  liveness ok = {healthOk}");

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

    // The Host finds an audit entry by trace id through an index that only a rebuild fills; a forward call does not.
    var rebuilt = await client.RebuildIndexAsync();
    var traced = await client.GetAuditByTraceAsync(answer.TraceId);
    Console.WriteLine($"Rebuild: {rebuilt.AuditEntriesIndexed} audit entries indexed, {rebuilt.ModulesSnapshot} modules snapshotted");
    Console.WriteLine($"Trace:   {traced.TraceId} asked \"{traced.Inputs["Question"].GetString()}\"");

    // Replay serves the recorded answer; the model is not called again.
    // Only the scripted model can be asked how often it was called.
    var callsBeforeReplay = serviceModel?.RecordedCalls.Count;
    var replayed = await client.ReplayAsync(answer.TraceId);
    var replayOk = replayed.Outputs["Answer"].GetString() == answerText
        && (serviceModel is null || serviceModel.RecordedCalls.Count == callsBeforeReplay);
    Console.WriteLine($"Replay:  {replayed.Outputs["Answer"].GetString()}"
        + (serviceModel is null ? "" : $" (model calls before {callsBeforeReplay}, after {serviceModel.RecordedCalls.Count})"));

    // Prompt versions: a challenger is stored, promoted, and then reported as the active version.
    var challenger = await client.CreatePromptVersionAsync(AgentServiceHost.ModuleName, new CreatePromptVersionRequest(
        new Dictionary<string, IReadOnlyList<DemoExample>>
        {
            ["default"] = [Example(Question, answerText)],
        },
        new Dictionary<string, string> { ["default"] = "Answer in one short sentence." },
        Note: "sample"));
    var promoted = await client.PromoteAsync(AgentServiceHost.ModuleName, challenger.VersionId);
    var active = await client.GetActivePromptAsync(AgentServiceHost.ModuleName);
    var promptsOk = promoted.ActiveVersionId == challenger.VersionId && active.VersionId == challenger.VersionId
        && active.Status == PromptStatus.Active;
    Console.WriteLine($"Prompts: {challenger.VersionId} promoted, active = {active.VersionId}, status {active.Status}");

    // Optimize is plumbing: the Host scores every run 1.0, so it collects the module's own answers and tunes nothing.
    var run = await client.StartOptimizeAsync(AgentServiceHost.ModuleName, new OptimizeRequest(
        OptimizerTypes.BootstrapFewShot,
        new InlineExamples([.. trainingQuestions.Select(q => Example(q, answerText))]),
        new Dictionary<string, JsonElement>()));
    var finished = await client.WaitForOptimizeAsync(run.RunId, TimeSpan.FromSeconds(60));
    var optimizeFrames = await client.StreamOptimizeAsync(run.RunId);
    var optimizeOk = finished.State == OptimizeRunState.Succeeded
        && finished.ResultPromptVersionId is not null
        && optimizeFrames.Select(f => f.Event).SequenceEqual(["final", "done"]);
    Console.WriteLine($"Optimize: run {run.RunId} {finished.State}, {finished.TrialsCompleted} of {finished.TrialsTotal} examples, version {finished.ResultPromptVersionId}, stream {string.Join(" ", optimizeFrames.Select(f => f.Event))}");

    var openApi = await client.GetOpenApiAsync();
    var routes = openApi.GetProperty("paths").EnumerateObject().Count();
    Console.WriteLine($"OpenAPI: {routes} routes described");
    await app.StopAsync();

    // The Host's own command line indexes an audit file it finds under ./data in its working directory.
    var hostCliDir = Path.Combine(workDir, "host-cli");
    Directory.CreateDirectory(Path.Combine(hostCliDir, "data"));
    await using (var source = new FileStream(Path.Combine(workDir, "audit.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
    await using (var target = File.Create(Path.Combine(hostCliDir, "data", "audit.jsonl")))
        await source.CopyToAsync(target);
    var hostCli = await HostRebuildCli.RunAsync(hostCliDir, []);
    Console.WriteLine($"Host CLI: exit {hostCli.ExitCode}, {hostCli.Output.Trim()}");

    // 2. A ReAct agent whose tool comes from an MCP tool source.
    using var tools = new ToolAgent(Model(() => FakeScript.ToolModel("A-1001")), new OrderToolSource());
    Console.WriteLine($"Tools:   {string.Join(", ", await tools.ToolNamesAsync())}");
    Console.WriteLine($"Agent:   {await tools.AskAsync(OrderQuestion)}");
    foreach (var survey in await ToolAgent.SurveyAsync(new OrderToolTransport(), OrderToolTransport.ServerUri))
        Console.WriteLine($"Survey:  {survey.Name} declared Safe, governed as {survey.DangerTier}, confirmation required = {survey.RequiresConfirmation}");

    // The same tool served as an MCP server over JSON-RPC on loopback, and called back through the library's SDK transport.
    var mcpBuilder = WebApplication.CreateBuilder();
    mcpBuilder.WebHost.UseUrls("http://127.0.0.1:0");
    mcpBuilder.Logging.SetMinimumLevel(LogLevel.Warning);
    var mcpServer = new OrderMcpServer();
    await using var mcpApp = mcpServer.Map(mcpBuilder.Build());
    await mcpApp.StartAsync();
    var mcpUrl = new Uri(new Uri(mcpApp.Urls.First()), OrderMcpServer.Route).ToString();
    await using var transport = SdkMcpTransport.CreateHttp(new McpServerConnection(mcpUrl, TrustLevel.Unknown));
    var served = await transport.ListToolsAsync();
    var direct = await transport.CallToolAsync(OrderMcpServer.ToolName, new Dictionary<string, object?> { ["orderId"] = "A-1001" });
    Console.WriteLine($"MCP:     {mcpUrl} tools/list = {string.Join(", ", served.Select(t => t.Name))}");
    Console.WriteLine($"MCP:     tools/call = {TransportToolSource.Text(direct.Result)}");
    foreach (var survey in await ToolAgent.SurveyAsync(transport, mcpUrl))
        Console.WriteLine($"MCP:     surveyed {survey.Name} declared Safe, governed as {survey.DangerTier}");
    using var servedAgent = new ToolAgent(Model(() => FakeScript.ToolModel("A-1001")), new TransportToolSource(transport));
    var servedAnswer = await servedAgent.AskAsync(OrderQuestion);
    Console.WriteLine($"MCP:     agent through the served tool = {servedAnswer}");
    var toolAudit = await mcpServer.Dispatcher.AuditLog.QueryAsync(OrderMcpServer.ToolName);
    var mcpOk = served.Count == 1 && direct.Success && servedAnswer == OrderData.Lookup("A-1001")
        && toolAudit.Count(e => e.EventType == "invocation.completed") == 2;

    // The governed path over HTTP: with no verifier the cap blocks the tool and the server sees no call; with
    // one that trusts the declared tier the call goes through the dispatcher and reaches the server.
    var receivedBefore = mcpServer.ReceivedToolCalls.Count;
    await using (var capped = await GovernedMcpClient.ConnectAsync(
        SdkMcpTransport.CreateHttp(new McpServerConnection(mcpUrl, TrustLevel.Unknown)), mcpUrl, verifier: null))
    {
        var blocked = await capped.CallAsync(OrderMcpServer.ToolName, "A-1001");
        Console.WriteLine($"MCP:     governed http, no verifier: success = {blocked.Success}, server saw {mcpServer.ReceivedToolCalls.Count - receivedBefore} calls");
        mcpOk &= !blocked.Success && mcpServer.ReceivedToolCalls.Count == receivedBefore;
    }
    await using (var trusting = await GovernedMcpClient.ConnectAsync(
        SdkMcpTransport.CreateHttp(new McpServerConnection(mcpUrl, TrustLevel.Unknown)), mcpUrl, new DeclaredTierVerifier()))
    {
        var allowed = await trusting.CallAsync(OrderMcpServer.ToolName, "A-1001");
        Console.WriteLine($"MCP:     governed http, declared tier trusted: {allowed.Output}");
        mcpOk &= allowed.Success && allowed.Output == OrderData.Lookup("A-1001")
            && mcpServer.ReceivedToolCalls.Count == receivedBefore + 1;
    }
    // Close the session before the server stops, so Kestrel has no open request to wait for.
    await transport.DisposeAsync();
    await mcpApp.StopAsync();

    // The same governed path over stdio. The server is this program started again; the SDK starts the child
    // and owns it, and disposing the transport ends it.
    var stdioDir = Path.Combine(workDir, "mcp-stdio");
    Directory.CreateDirectory(stdioDir);
    var callLog = Path.Combine(stdioDir, "calls.log");
    var pidFile = Path.Combine(stdioDir, "child.pid");
    var stdioOk = false;
    System.Diagnostics.Process child;
    await using (var stdio = await GovernedMcpClient.ConnectAsync(
        SdkMcpTransport.CreateStdio(OrderMcpStdio.Connection(callLog, pidFile)), OrderMcpStdio.ServerUri, new DeclaredTierVerifier()))
    {
        child = OrderMcpStdio.OpenChild(pidFile);
        foreach (var survey in stdio.Tools)
            Console.WriteLine($"MCP:     stdio {survey.Name} governed as {survey.DangerTier}, confirmation required = {survey.RequiresConfirmation}");
        var safe = await stdio.CallAsync(OrderMcpStdio.SafeTool, "A-1001");
        var purge = await stdio.CallAsync(OrderMcpStdio.CatastrophicTool, "A-1001");
        var note = await stdio.CallAsync(OrderMcpStdio.UnannotatedTool, "A-1001");
        Console.WriteLine($"MCP:     stdio {OrderMcpStdio.SafeTool} = {safe.Output}");
        Console.WriteLine($"MCP:     stdio {OrderMcpStdio.CatastrophicTool} success = {purge.Success}, {OrderMcpStdio.UnannotatedTool} success = {note.Success}");
        stdioOk = safe.Success && safe.Output == OrderData.Lookup("A-1001") && !purge.Success && !note.Success;
    }
    using (child)
    {
        var exited = child.WaitForExit(TimeSpan.FromSeconds(10));
        var serverSaw = (await File.ReadAllLinesAsync(callLog)).ToArray();
        Console.WriteLine($"MCP:     stdio server received [{string.Join(", ", serverSaw)}], child exited after dispose = {exited}");
        stdioOk &= exited && serverSaw.SequenceEqual([OrderMcpStdio.SafeTool]);
    }
    mcpOk &= stdioOk;

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

    // 5. `ampelum new` scaffolds a project from the template; the generated project is not built here.
    var generated = Path.Combine(workDir, "generated");
    var scaffold = await CliNew.RunAsync("Orders.Agent", generated, Path.Combine(workDir, "engine-home"));
    var generatedFiles = Directory.Exists(generated) ? Directory.GetFiles(generated, "*", SearchOption.AllDirectories).Length : 0;
    Console.WriteLine($"CLI new: exit {scaffold.ExitCode}, {generatedFiles} files written for {CliNew.Template}");

    // 6. `ampelum repl` needs a real key. With the key blank it must refuse and say which variable it wants.
    var repl = await CliRepl.RunWithBlankKeyAsync();
    var replRefused = repl.ExitCode != 0 && repl.Output.Contains(CliRepl.ApiKeyVariable, StringComparison.Ordinal);
    Console.WriteLine($"CLI repl: exit {repl.ExitCode}, names {CliRepl.ApiKeyVariable} = {replRefused}");

    var hostCliOk = hostCli.ExitCode == 0 && hostCli.Output.Contains("audit entries", StringComparison.Ordinal);
    var scaffoldOk = scaffold.ExitCode == 0 && generatedFiles == 6;
    return verify.Ok && streamOk && healthOk && replayOk && promptsOk && optimizeOk && routes > 0
           && hostCliOk && mcpOk && replErr.Length == 0 && cliExit == 0 && scaffoldOk && replRefused ? 0 : 1;
}
finally
{
    // The Host's SQLite connections are pooled and keep the database file open until the pools are cleared.
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    try
    {
        Directory.Delete(workDir, recursive: true);
    }
    catch (IOException)
    {
        // The Host's SQLite file can stay locked briefly; the directory is in the temp folder.
    }
}

static DemoExample Example(string question, string answer) => new(
    new Dictionary<string, JsonElement> { ["Question"] = JsonSerializer.SerializeToElement(question) },
    new Dictionary<string, JsonElement> { ["Answer"] = JsonSerializer.SerializeToElement(answer) });

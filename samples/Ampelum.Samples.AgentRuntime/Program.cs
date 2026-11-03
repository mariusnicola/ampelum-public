using Ampelum.Samples.AgentRuntime;
using Ampelum.Samples.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Agent.Contracts;

var fake = args.Contains("--fake");
IChatClient chat;
ModelTiers tiers;
if (fake)
{
    chat = new FakeTriageModel();
    tiers = FakeTriageModel.Tiers;
}
else
{
    var config = SampleConfig.Load();
    chat = config.CreateChatClient();
    tiers = ModelTiers.FromConfig(config);
}

var directory = Path.Combine(Path.GetTempPath(), "ampelum-agent-runtime", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var result = await TriageScenario.RunAsync(directory, chat, tiers, Console.Out);
    return result.Outcome.Status == WorkflowExecutionStatus.Succeeded && result.Outcome.ReportPath is not null ? 0 : 1;
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(directory, recursive: true);
}

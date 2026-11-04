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
    // The fix runs on the capable tier's model, outside the triage budget; its turn limit bounds it.
    IChatClient fixer = fake ? new FakeFixerModel(FakeFixerModel.PricingFix()) : new PinnedModelClient(chat, tiers.Capable);
    var result = await TriageScenario.RunAsync(
        directory, chat, tiers, Console.Out, fixer: fixer,
        fixerForTier: fake ? null : tier => new PinnedModelClient(chat, tiers.For(tier)));
    return result.Outcome.Status == WorkflowExecutionStatus.Succeeded && result.Outcome.ReportPath is not null && result.Fix?.Passed == true ? 0 : 1;
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(directory, recursive: true);
}

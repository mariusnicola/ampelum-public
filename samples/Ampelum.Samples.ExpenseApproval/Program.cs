using Ampelum.Samples.ExpenseApproval;
using Ampelum.Samples.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Agent.Contracts;

var fake = args.Contains("--fake");
var notes = new[]
{
    "Taxi to the client site, Acme Taxi, 42.50 EUR, travel.",
    "Conference ticket from DevSummit, 1800 EUR, training.",
};

IChatClient chat = fake
    ? FakeExpenseClient.Create(new Expense("Acme Taxi", 42.50m, "travel"), new Expense("DevSummit", 1800m, "training"))
    : SampleConfig.Load().CreateChatClient();

var directory = Path.Combine(Path.GetTempPath(), "ampelum-expense-approval", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var pipeline = new ExpenseApprovalPipeline(chat, new InMemoryExpenseLedger(Console.WriteLine), ExpenseApprovalPipeline.LoadDefinition(), Path.Combine(directory, "workflows.db"));
    var failed = false;
    foreach (var note in notes)
    {
        Console.WriteLine($"Note: {note}");
        var result = await pipeline.RunWithRestartAsync(note);
        Console.WriteLine($"  [{result.Status}] tool={result.ToolExecuted} audit={string.Join(",", result.Audit.Select(e => e.EventType))}");
        failed |= result.Status != WorkflowExecutionStatus.Succeeded;
    }
    return failed ? 1 : 0;
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(directory, recursive: true);
}

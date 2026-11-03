using Ampelum.Samples.Shared;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Agent.Contracts;

namespace Ampelum.Samples.ExpenseApproval;

/// <summary>Outcome of one command-line run: the exit code and every execution id the run created.</summary>
public sealed record ExpenseRunOutcome(int ExitCode, IReadOnlyList<string> ExecutionIds);

/// <summary>The command-line path of the sample, kept out of Program.cs so a test can run it.</summary>
public static class ExpenseRun
{
    static readonly string[] Notes =
    [
        "Taxi to the client site, Acme Taxi, 42.50 EUR, travel.",
        "Conference ticket from DevSummit, 1800 EUR, training.",
    ];

    /// <summary>
    /// Runs both notes. <c>--fake</c> scripts the model, <c>--postgres</c> keeps the state in PostgreSQL.
    /// The rows written for each execution are deleted when the run ends, also after a failure.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="directory">Folder for the SQLite file; the caller creates and removes it.</param>
    /// <param name="write">Receives the lines the run prints.</param>
    /// <param name="readSetting">Reads a required setting by name; null reads <see cref="SampleConfig"/>.</param>
    /// <param name="beforeCleanup">Called with the execution ids after the run and before its rows are deleted.</param>
    public static async Task<ExpenseRunOutcome> RunAsync(
        string[] args,
        string directory,
        Action<string> write,
        Func<string, string>? readSetting = null,
        Func<IReadOnlyList<string>, Task>? beforeCleanup = null)
    {
        var fake = args.Contains("--fake");
        var postgres = args.Contains("--postgres");
        var executionIds = new List<string>();
        ExpenseStore? cleanup = null;
        try
        {
            // Settings are read only when a live model or PostgreSQL needs them.
            var config = new Lazy<SampleConfig>(() => SampleConfig.Load());
            Func<string, string> require = readSetting ?? (key => config.Value.Require(key));
            var sqlitePath = Path.Combine(directory, "workflows.db");
            // The factory builds a new persistence per runtime, so a resumed run shares nothing with the first.
            Func<ExpenseStore> openStore = () => ExpenseStore.Create(postgres, require, sqlitePath);
            cleanup = openStore();

            IChatClient chat = fake
                ? FakeExpenseClient.Create(new Expense("Acme Taxi", 42.50m, "travel"), new Expense("DevSummit", 1800m, "training"))
                : config.Value.CreateChatClient();
            var pipeline = new ExpenseApprovalPipeline(chat, new InMemoryExpenseLedger(write), ExpenseApprovalPipeline.LoadDefinition(), openStore);
            var failed = false;
            foreach (var note in Notes)
            {
                write($"Note: {note}");
                var executionId = Guid.NewGuid().ToString("N");
                executionIds.Add(executionId);
                var result = await pipeline.RunWithRestartAsync(note, executionId: executionId);
                write($"  [{result.Status}] tool={result.ToolExecuted} audit={string.Join(",", result.Audit.Select(e => e.EventType))}");
                failed |= result.Status != WorkflowExecutionStatus.Succeeded;
            }
            return new ExpenseRunOutcome(failed ? 1 : 0, executionIds);
        }
        finally
        {
            if (beforeCleanup is not null)
                await beforeCleanup(executionIds);
            if (cleanup is not null)
                foreach (var id in executionIds)
                    await cleanup.DeleteExecutionAsync(id);
        }
    }
}

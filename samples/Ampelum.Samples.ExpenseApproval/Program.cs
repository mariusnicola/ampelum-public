using Ampelum.Samples.ExpenseApproval;
using Microsoft.Data.Sqlite;

var directory = Path.Combine(Path.GetTempPath(), "ampelum-expense-approval", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    return (await ExpenseRun.RunAsync(args, directory, Console.WriteLine)).ExitCode;
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(directory, recursive: true);
}

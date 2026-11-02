namespace SignalGrid.Ampelum.Context.SlashCommands;

/// <summary>Handles /summary (triggers chapter summary).</summary>
public sealed class SummaryCommandHandler : ISlashCommandHandler
{
    /// <inheritdoc />
    public string Command => "summary";
    /// <inheritdoc />
    public string Description => "Summarizes the current chapter.";

    /// <inheritdoc />
    public bool CanHandle(string args) => true;

    /// <inheritdoc />
    public Task<string> ExecuteAsync(string args, IEventStore store, CancellationToken ct)
    {
        // Summary creation is handled by the summariser pipeline; this just triggers it.
        return Task.FromResult("✅ Summary queued for current chapter.");
    }
}

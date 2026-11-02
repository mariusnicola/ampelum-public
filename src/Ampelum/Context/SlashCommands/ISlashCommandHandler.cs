namespace SignalGrid.Ampelum.Context.SlashCommands;

/// <summary>Handle for a single slash command (e.g., /chapter, /summary).</summary>
public interface ISlashCommandHandler
{
    /// <summary>Command name without leading slash (e.g., "chapter").</summary>
    string Command { get; }
    /// <summary>Description shown in /help.</summary>
    string Description { get; }
    /// <summary>Returns true if the handler can process the given args.</summary>
    bool CanHandle(string args);
    /// <summary>Executes the command. Returns a user-visible result message.</summary>
    Task<string> ExecuteAsync(string args, IEventStore store, CancellationToken ct);
}

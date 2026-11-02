using System.Text.RegularExpressions;

namespace SignalGrid.Ampelum.Context.SlashCommands;

/// <summary>Dispatches slash commands to registered handlers. Thread-safe after construction.</summary>
public sealed partial class SlashCommandDispatcher
{
    private readonly Dictionary<string, ISlashCommandHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers a command handler. Throws if duplicate command name.</summary>
    public void Register(ISlashCommandHandler handler)
    {
        if (!_handlers.TryAdd(handler.Command, handler))
            throw new InvalidOperationException($"Slash command /{handler.Command} already registered.");
    }

    /// <summary>Parses and dispatches a raw input line. Returns null if no /command matched.</summary>
    public async Task<string?> TryDispatchAsync(string input, IEventStore store, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(input) || !input.StartsWith("/"))
            return null;

        var m = SlashRegex().Match(input);
        if (!m.Success) return null;

        var cmd = m.Groups[1].Value;
        var args = m.Groups[2].Value.Trim();

        if (_handlers.TryGetValue(cmd, out var handler))
            return await handler.ExecuteAsync(args, store, ct);

        return null;
    }

    [GeneratedRegex(@"\\/(\\w+)(?:\\s+(.*))?$")]
    private static partial Regex SlashRegex();
}

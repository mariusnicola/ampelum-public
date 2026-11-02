using SignalGrid.Ampelum.Context.Chapters;

namespace SignalGrid.Ampelum.Context.SlashCommands;

/// <summary>Handles /chapter (start/manual chapter boundary).</summary>
public sealed class ChapterCommandHandler : ISlashCommandHandler
{
    /// <inheritdoc />
    public string Command => "chapter";
    /// <inheritdoc />
    public string Description => "Closes the current chapter and starts a new one.";

    /// <inheritdoc />
    public bool CanHandle(string args) => true;

    /// <inheritdoc />
    public async Task<string> ExecuteAsync(string args, IEventStore store, CancellationToken ct)
    {
        await using var w = await store.AcquireWriterLockAsync(ct);
        var turn = await store.CurrentTurnAsync(ct);
        var seq = await store.NextSeqAsync(turn, ct);
        var boundary = new ChapterBoundary(turn, seq, DateTimeOffset.UtcNow,
            $"chapter:{turn}:{seq}:{Guid.NewGuid():N}", null,
            Guid.NewGuid().ToString("N")[..12], args);
        await store.AppendAsync(boundary, ct);
        return $"✅ New chapter started. Current turn: {turn}";
    }
}

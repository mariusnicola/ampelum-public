using Microsoft.Extensions.AI;

namespace Ampelum.Samples.ComposedPrograms.Style;

/// <summary>Runs the retrieval variants and the index save and restore, each on its own pipeline.</summary>
public static class StyleDemo
{
    /// <summary>The style notes every retrieval mode finds for one change.</summary>
    public static async Task<IReadOnlyDictionary<RetrievalMode, string>> ModesAsync(
        IChatClient chat, ChangeSource source, ChangeRow row, ReleaseNotesOptions options)
    {
        var notes = new Dictionary<RetrievalMode, string>();
        foreach (var mode in Enum.GetValues<RetrievalMode>())
        {
            await using var pipeline = new ReleaseNotesPipeline(chat, source, options with { StyleRetrieval = mode });
            notes[mode] = await pipeline.StyleNotesAsync(row).ConfigureAwait(false);
        }
        return notes;
    }

    /// <summary>Builds and saves an index, restores it in a second pipeline and asks both the same question.</summary>
    public static async Task<RoundTrip> RoundTripAsync(
        IChatClient chat, ChangeSource source, ChangeRow row, ReleaseNotesOptions options, string indexPath)
    {
        var withPath = options with { StyleIndexPath = indexPath };
        await using var first = new ReleaseNotesPipeline(chat, source, withPath);
        var built = await first.StyleNotesAsync(row).ConfigureAwait(false);
        var existedAfterBuild = File.Exists(indexPath);
        await using var second = new ReleaseNotesPipeline(chat, source, withPath);
        var restored = await second.StyleNotesAsync(row).ConfigureAwait(false);
        return new RoundTrip(built, restored, existedAfterBuild);
    }
}

public sealed record RoundTrip(string Built, string Restored, bool FileWritten);

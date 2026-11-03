using Ampelum.Samples.ComposedPrograms.Drafting;
using Ampelum.Samples.ComposedPrograms.Fake;
using Microsoft.Extensions.Logging.Abstractions;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Testing;

namespace Ampelum.Samples.ComposedPrograms.Tests;

/// <summary>Builds the pieces most tests need, with no environment, network or clock involved.</summary>
internal static class Rig
{
    /// <summary>Options with no retry delay, so no test waits.</summary>
    public static ReleaseNotesOptions Options(Func<ReleaseNotesOptions, ReleaseNotesOptions>? tweak = null)
    {
        var options = new ReleaseNotesOptions { RetryDelay = TimeSpan.Zero };
        return tweak is null ? options : tweak(options);
    }

    public static AmpelumContext Context(ScriptedModel model, CapturingTraceSink? sink = null) =>
        new(model, new ChatAdapter(), sink ?? new CapturingTraceSink(), NullLoggerFactory.Instance);

    public static NoteDrafter Drafter(ScriptedModel model, ReleaseNotesOptions? options = null, IStyleNotes? style = null) =>
        new(Context(model), options ?? Options(), style ?? new NoStyleNotes(), NullLogger.Instance);

    public static string DataDirectory => Path.Combine(AppContext.BaseDirectory, "data");

    public static ChangeSource Source() => new(DataDirectory);

    sealed class NoStyleNotes : IStyleNotes
    {
        public Task<string> NotesForAsync(string title, string details, CancellationToken ct) => Task.FromResult("");
    }
}

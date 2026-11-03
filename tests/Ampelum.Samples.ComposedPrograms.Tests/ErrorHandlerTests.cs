using Ampelum.Samples.ComposedPrograms.Drafting;
using Ampelum.Samples.ComposedPrograms.Fake;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Ampelum.Samples.ComposedPrograms.Tests;

/// <summary>A failing model call is retried, then replaced by a note built from the title.</summary>
public sealed class ErrorHandlerTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>Fails the first <paramref name="failures"/> draft calls, then lets the normal rules answer.</summary>
    static ScriptedModel Flaky(int failures)
    {
        var seen = 0;
        return new ScriptedModel()
            .When("flaky", call =>
            {
                if (!call.Mentions(Helpers.DraftPhrase) || Interlocked.Increment(ref seen) > failures)
                    return null;
                throw new InvalidOperationException("model unavailable");
            })
            .Include(DraftingRules.Create());
    }

    static NoteDrafter Drafter(ScriptedModel model, RecordingLogger logger, ReleaseNotesOptions options) =>
        new(Rig.Context(model), options, new NoStyle(), logger);

    sealed class NoStyle : IStyleNotes
    {
        public Task<string> NotesForAsync(string title, string details, CancellationToken ct) => Task.FromResult("");
    }

    [Fact]
    public async Task One_failure_is_retried_and_the_second_attempt_is_used()
    {
        var model = Flaky(1);
        var logger = new RecordingLogger();

        var (note, _) = await Drafter(model, logger, Rig.Options()).DraftAsync(await Helpers.RowAsync("C-106"), Ct);

        model.Drafts().Count().ShouldBe(2);
        note.Note.ShouldBe("Refactored autosave module.");
        logger.Entries.Count(e => e.Level == LogLevel.Warning).ShouldBe(1);
    }

    [Fact]
    public async Task Failures_past_the_retry_limit_fall_back_to_the_title()
    {
        var model = Flaky(int.MaxValue);
        var logger = new RecordingLogger();

        var (note, _) = await Drafter(model, logger, Rig.Options()).DraftAsync(await Helpers.RowAsync("C-106"), Ct);

        model.Drafts().Count().ShouldBe(2);
        note.Note.ShouldBe("Refactor autosave module");
    }

    [Fact]
    public async Task The_retry_count_comes_from_the_options()
    {
        var model = Flaky(int.MaxValue);
        var options = Rig.Options(o => o with { DraftRetries = 0 });

        await Drafter(model, new RecordingLogger(), options).DraftAsync(await Helpers.RowAsync("C-106"), Ct);

        model.Drafts().Count().ShouldBe(1);
    }
}

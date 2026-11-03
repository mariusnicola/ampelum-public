using Ampelum.Samples.ComposedPrograms.Fake;
using Xunit;

namespace Ampelum.Samples.ComposedPrograms.Tests;

internal static class Helpers
{
    public const string DraftPhrase = "Write one release note line";

    public static async Task<ChangeRow> RowAsync(string id) =>
        (await Rig.Source().LoadChangesAsync(TestContext.Current.CancellationToken)).Single(r => r.Id == id);

    /// <summary>The note the drafter writes for a change, for tests that need its kind and impact.</summary>
    public static async Task<DraftedNote> NoteAsync(string id) =>
        (await Rig.Drafter(DraftingRules.Create()).DraftAsync(await RowAsync(id), TestContext.Current.CancellationToken)).Note;

    public static SignalGrid.Ampelum.TraceRecord Trace() =>
        new(Guid.NewGuid(), "test", typeof(string), new Dictionary<string, object?>(), new Dictionary<string, object?>(),
            [], "", null, TimeSpan.Zero, null, new Dictionary<string, object?>());

    public static int Count(this ScriptedModel model, string phrase) => model.Calls.Count(c => c.Mentions(phrase));

    public static IEnumerable<ModelCall> Drafts(this ScriptedModel model) => model.Calls.Where(c => c.Mentions(DraftPhrase));
}

using System.Globalization;
using Ampelum.Samples.ComposedPrograms;
using Ampelum.Samples.ComposedPrograms.Fake;
using Ampelum.Samples.ComposedPrograms.Style;
using Ampelum.Samples.ComposedPrograms.Tuning;
using Ampelum.Samples.Shared;
using Microsoft.Extensions.AI;

// Numbers print the same on every machine.
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

var fake = args.Contains("--fake");
IChatClient chat = fake ? ReleaseScript.Create() : SampleConfig.Load().CreateChatClient();
// The extra stages make many model calls, so a live run does them only when asked.
var evaluate = fake || args.Contains("--evaluate");
var tune = fake || args.Contains("--tune");
var extras = fake || args.Contains("--extras");

var source = new ChangeSource(Path.Combine(AppContext.BaseDirectory, "data"));
await using var pipeline = new ReleaseNotesPipeline(chat, source);

var release = await pipeline.RunAsync();
Console.WriteLine($"Headline: {release.Headline}");
foreach (var note in release.Notes)
    Console.WriteLine($"  {note.Id} {note.Kind,-9} impact {note.Impact} quality {note.Quality:0.00} tightened {note.TightenPasses}x  {note.Note}");
Console.WriteLine($"Review:   {string.Join(", ", release.Review.Speakers)} -> {release.Review.Verdict}");
foreach (var (id, verdict) in release.FactChecks)
    Console.WriteLine($"Fact check {id}: {verdict}");
Console.WriteLine($"Announce: {release.Announcement}");

var changes = await source.LoadChangesAsync();
var tags = await pipeline.TagAsync(changes[0]);
Console.WriteLine($"Tags:     {string.Join(", ", tags.Select(t => $"{t.Key}={t.Value}"))}");

if (evaluate)
{
    foreach (var summary in new[] { await pipeline.EvaluateClassificationAsync(), await pipeline.EvaluateDraftingAsync() })
        foreach (var score in summary.Scores)
            Console.WriteLine($"Metric {score.Metric,-20} mean {score.Mean:0.000}");
}

if (tune)
{
    var tuned = await pipeline.TuneAsync(PromptTuning.DefaultOptions);
    var scripted = fake ? ", scripted under --fake" : "";
    Console.WriteLine(
        $"Tuning:   ROUGE-L {tuned.BaselineRougeL:0.000} -> {tuned.TunedRougeL:0.000} (measured on the notes it learned from{scripted})");
}

if (extras)
{
    // Rows are chosen by what the classifier decided, so editing the changes file cannot break the picks.
    var internalNote = Pick(release, ChangeKind.Internal);
    var fixNote = Pick(release, ChangeKind.Fix);
    var internalChange = changes.Single(c => c.Id == internalNote.Id);
    var fix = changes.Single(c => c.Id == fixNote.Id);

    // An internal change normally gets one plain call; lowering the impact bar sends it to Refine.
    await using (var refining = new ReleaseNotesPipeline(chat, source, new ReleaseNotesOptions { ComposedImpact = 1 }))
    {
        var (refined, route) = await refining.DraftOneAsync(internalChange);
        Console.WriteLine($"Refine:   {route.Strategy} -> {refined.Note}");
    }

    foreach (var (mode, notes) in await StyleDemo.ModesAsync(chat, source, fix, new ReleaseNotesOptions()))
        Console.WriteLine($"Style {mode,-11} {notes.Replace('\n', ' ')}");

    // The round trip writes the index to a temporary file and deletes it afterwards.
    var indexPath = Path.Combine(Path.GetTempPath(), $"release-style-{Guid.NewGuid():N}.json");
    try
    {
        var trip = await StyleDemo.RoundTripAsync(chat, source, fix, new ReleaseNotesOptions(), indexPath);
        Console.WriteLine($"Index:    saved {trip.FileWritten}, restored notes equal built notes {trip.Built == trip.Restored}");
    }
    finally
    {
        File.Delete(indexPath);
    }

    var retrieval = await pipeline.EvaluateRetrievalAsync(2);
    Console.WriteLine($"Retrieval: {string.Join(", ", retrieval.Means.Select(m => $"{m.Key} {m.Value:0.00}"))}");

    var (knnNote, neighbours) = await pipeline.DraftWithNeighboursAsync(fix, fixNote);
    Console.WriteLine($"Knn:      {neighbours.Count} neighbours -> {knnNote}");
    var (ensembleNote, members) = await pipeline.DraftWithEnsembleAsync(fix, fixNote);
    Console.WriteLine($"Ensemble: {members} members -> {ensembleNote}");
}

return release.Review.Approved ? 0 : 1;

static DraftedNote Pick(ReleaseResult release, ChangeKind kind) =>
    release.Notes.FirstOrDefault(n => n.Kind == kind)
    ?? throw new InvalidOperationException(
        $"The extra stages need at least one change that is classified as {kind}, and the changes file has none.");

using System.Text.RegularExpressions;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Metrics;

namespace Ampelum.Samples.ComposedPrograms.Drafting;

/// <summary>
/// Scores a drafted note against the change it describes: how many key words of the title it keeps,
/// scaled down when it runs past the word limit. Needs no reference note, so it works on a new release.
/// </summary>
public sealed partial class NoteQuality(int maxWords) : IMetric<DraftSig>, IRefinableFeedback
{
    [GeneratedRegex(@"\p{L}{4,}")]
    private static partial Regex WordPattern();

    public double Score(DraftSig actual, DraftSig inputs, SignatureMetadata signature)
    {
        var terms = KeyTerms(inputs.Title);
        var covered = terms.Count(t => actual.Note.Contains(t, StringComparison.OrdinalIgnoreCase));
        var coverage = terms.Count == 0 ? 1.0 : (double)covered / terms.Count;
        var words = WordCount.Of(actual.Note);
        var fit = words <= maxWords ? 1.0 : (double)maxWords / words;
        return coverage * fit;
    }

    public string GetFeedback(object actual, object expected, SignatureMetadata signature)
    {
        var note = ((DraftSig)actual).Note;
        var missing = KeyTerms(((DraftSig)expected).Title)
            .Where(t => !note.Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();
        var parts = new List<string>();
        if (missing.Count > 0)
            parts.Add($"mention {string.Join(", ", missing)}");
        if (WordCount.Of(note) > maxWords)
            parts.Add($"stay within {maxWords} words");
        return parts.Count == 0 ? "keep the note as it is" : string.Join(" and ", parts);
    }

    internal static IReadOnlyList<string> KeyTerms(string title) =>
        WordPattern().Matches(title).Select(m => m.Value.ToLowerInvariant()).Distinct().ToList();
}

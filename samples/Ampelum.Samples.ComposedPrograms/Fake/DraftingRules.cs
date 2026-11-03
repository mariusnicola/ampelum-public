using System.Collections.Concurrent;
using System.Text.Json;
using SignalGrid.Ampelum;

namespace Ampelum.Samples.ComposedPrograms.Fake;

/// <summary>Scripted answers for classifying, drafting, tightening and the headline.</summary>
public static class DraftingRules
{
    /// <summary>Phrase the scripted tuner proposes; a draft call that carries it is answered well.</summary>
    public const string TunedInstruction =
        "Write the note as one sentence that starts with a past tense verb and names the change a user can see.";

    public static ScriptedModel Create()
    {
        var seen = new ConcurrentDictionary<string, int>();
        return new ScriptedModel()
            .When("classify", Classify)
            .When("tag", Tag)
            .When("tighten", Tighten)
            .When("headline-extraction", HeadlineExtraction)
            .When("headline", Headline)
            .When("aggregate", Aggregate)
            .When("draft", call => Draft(call, seen));
    }

    static string? Classify(ModelCall call)
    {
        if (!call.Mentions("Classify one product change"))
            return null;
        var fields = Wire.XmlFields(call.User);
        var kind = NoteWriter.Kind(fields["Title"], fields["Details"]);
        return Wire.Xml(("Kind", kind), ("Impact", NoteWriter.Impact(kind, fields["Title"]).ToString()));
    }

    /// <summary>
    /// A tuned instruction replaces the signature text, so a draft call is recognised by the fields it
    /// lists as well as by its instruction.
    /// </summary>
    static bool IsDraft(ModelCall call) =>
        call.Mentions("Write one release note line")
        || (call.Mentions("[[ ## StyleNotes ## ]]") && call.Mentions("[[ ## Note ## ]]"));

    static string? Draft(ModelCall call, ConcurrentDictionary<string, int> seen)
    {
        if (!IsDraft(call))
            return null;
        var fields = Wire.ChatFields(call.User);
        var title = fields["Title"];
        var withReasoning = call.Mentions("[[ ## reasoning ## ]]");
        var note = ChooseNote(call, fields, withReasoning, seen);
        return withReasoning
            ? Wire.Chat((TraceKeys.Reasoning, $"The change is described by its title: {title}."), ("Note", note))
            : Wire.Chat(("Note", note));
    }

    static string ChooseNote(ModelCall call, IReadOnlyDictionary<string, string> fields, bool withReasoning,
        ConcurrentDictionary<string, int> seen)
    {
        var title = fields["Title"];
        if (call.Mentions(TunedInstruction) || call.Mentions("The previous answer scored"))
            return NoteWriter.Good(title);
        if (call.Options?.Instructions is { } audience && audience.StartsWith("Audience:", StringComparison.Ordinal))
            return AudienceNote(audience, title);
        if (fields["Tone"].Length == 0)
            return NoteWriter.Plain(title);
        // The same prompt asked again is a new sample: writers differ from call to call.
        var nth = seen.AddOrUpdate(call.User, 0, (_, n) => n + 1);
        var order = withReasoning ? new[] { "good", "verbose", "weak" } : ["weak", "good", "verbose"];
        return order[nth % order.Length] switch
        {
            "good" => NoteWriter.Good(title),
            "verbose" => NoteWriter.Verbose(title),
            _ => NoteWriter.Weak(),
        };
    }

    static string AudienceNote(string audience, string title)
    {
        var good = NoteWriter.Good(title);
        return audience.Contains("developers", StringComparison.Ordinal) ? $"{good} Call the new API instead."
            : audience.Contains("administrators", StringComparison.Ordinal) ? $"{good} Check your integrations."
            : good;
    }

    static string? Aggregate(ModelCall call)
    {
        if (!call.Mentions("Synthesise a single best answer") || !IsDraft(call))
            return null;
        var fields = Wire.ChatFields(call.User);
        return Wire.Chat((TraceKeys.Reasoning, "The candidates agree on the change."), ("Note", NoteWriter.Good(fields["Title"])));
    }

    static string? Tag(ModelCall call)
    {
        if (!call.Mentions("Tag the change"))
            return null;
        var fields = Wire.ChatFields(call.User);
        var kind = NoteWriter.Kind(fields["Title"], fields["Details"]);
        return Wire.Chat(
            ("Component", fields["Title"].Split(' ')[^1]),
            ("Audience", kind == "Internal" ? "engineers" : "customers"));
    }

    static string? Tighten(ModelCall call)
    {
        if (!call.Mentions("Shorten the release note"))
            return null;
        var draft = Wire.ChatFields(call.User)["Draft"];
        var segments = draft.Split(" | ", StringSplitOptions.RemoveEmptyEntries);
        // Keep the first audience's wording; with one segment left, cut a quarter of the words.
        var shorter = segments.Length > 1
            ? segments[0]
            : string.Join(' ', draft.TrimEnd('.').Split(' ').Take(Math.Max(1, draft.Split(' ').Length * 3 / 4))) + ".";
        return Wire.Chat(("Note", shorter));
    }

    static string? Headline(ModelCall call)
    {
        if (!call.Mentions("Write a headline"))
            return null;
        var notes = Wire.ChatFields(call.User)["Notes"].Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var lead = notes[0].Split(": ", 2)[^1].TrimEnd('.');
        return $"Here is a headline for the release: {notes.Length} changes, led by: {lead}";
    }

    static string? HeadlineExtraction(ModelCall call)
    {
        if (!call.User.StartsWith("Extract the following fields", StringComparison.Ordinal))
            return null;
        var text = call.User[(call.User.IndexOf("Text: ", StringComparison.Ordinal) + "Text: ".Length)..].Trim();
        var headline = text[(text.IndexOf(": ", StringComparison.Ordinal) + 2)..];
        return JsonSerializer.Serialize(new { Headline = headline });
    }
}

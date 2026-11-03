using Ampelum.Samples.ComposedPrograms.Style;

namespace Ampelum.Samples.ComposedPrograms.Fake;

/// <summary>Scripted answers for the query rewriting steps of the style retrievers.</summary>
public static class StyleRules
{
    public static ScriptedModel Create() => new ScriptedModel()
        .When("rewrite", call => call.Mentions("Rewrite the change as one past tense release note sentence")
            ? NoteWriter.Good(FirstSentence(call.User).TrimEnd('.'))
            : null)
        .When("hyde", call => call.Mentions("hypothetical answer")
            ? $"{NoteWriter.Good(FirstSentence(call.User).TrimEnd('.'))} Users see this change in the product."
            : null)
        .When("multi-query", call => call.Mentions("paraphrases")
            ? string.Join("\n", Enumerable.Range(1, StyleRetrieval.Paraphrases).Select(i => $"{FirstSentence(call.User)} variant {i}"))
            : null)
        .When("step-back", call => call.Mentions("more abstract")
            ? "How are past release notes about fixes worded?"
            : null)
        .When("sub-questions", call => call.Mentions("Decompose")
            ? string.Join("\n", ["How is a fix worded?", FirstSentence(call.User)])
            : null)
        .When("route", call => call.Mentions("Reply with one word")
            ? (call.User.Contains("wording", StringComparison.OrdinalIgnoreCase) ? "style"
                : call.User.Contains("earlier", StringComparison.OrdinalIgnoreCase) ? "history" : "default")
            : null);

    static string FirstSentence(string text) => text.Split('.', 2)[0].Trim() + ".";
}

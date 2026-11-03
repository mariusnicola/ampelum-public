using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Ampelum.Samples.ComposedPrograms.Fake;

/// <summary>Scripted answers for the review panel, its memory, the fact check and the announcement.</summary>
public static partial class ReviewRules
{
    [GeneratedRegex(@"ChangeId:\s*(?<id>\S+)")]
    private static partial Regex ChangeId();

    [GeneratedRegex(@"^(?<name>\w+) \(", RegexOptions.Multiline)]
    private static partial Regex Speaker();

    public static ScriptedModel Create() => new ScriptedModel()
        .When("select-speaker", SelectSpeaker)
        .When("summarize", Summarize)
        .When("review", Review)
        .WhenMessage("fact-check", FactCheck)
        .WhenMessage("announce", Announce);

    static string? SelectSpeaker(ModelCall call)
    {
        if (!call.Mentions("Pick the next speaker"))
            return null;
        var history = Wire.ChatFields(call.User)["History"];
        var last = Speaker().Matches(history).LastOrDefault()?.Groups["name"].Value;
        var next = last switch { "Edith" => "Carl", "Carl" => "Pia", _ => "Edith" };
        return Wire.Chat(("NextSpeaker", next));
    }

    static string? Summarize(ModelCall call)
    {
        if (!call.Mentions("Summarize this conversation segment"))
            return null;
        var lines = Wire.ChatFields(call.User)["Conversation"].Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        return Wire.Chat(("Summary", $"The panel exchanged {lines} messages about the release notes."));
    }

    static string? Review(ModelCall call)
    {
        if (!call.Mentions("Review the release notes for tone"))
            return null;
        var fields = Wire.ChatFields(call.User);
        var conversation = fields["Conversation"];
        var opinion = fields["Role"] switch
        {
            "editor" => "Tone is plain and past tense.",
            "compliance" => MigrationAdvice(conversation),
            _ => conversation.Contains("revise:", StringComparison.Ordinal)
                ? "revise: fix the migration advice first."
                : "approve: the notes are ready.",
        };
        return Wire.Chat(("Opinion", opinion));
    }

    /// <summary>A breaking note without a pointer to what to do instead needs a revision.</summary>
    static string MigrationAdvice(string conversation)
    {
        var missing = conversation.Split('\n')
            .Where(line => line.StartsWith("Breaking:", StringComparison.Ordinal))
            .Where(line => !line.Contains("instead", StringComparison.OrdinalIgnoreCase)
                           && !line.Contains("integrations", StringComparison.OrdinalIgnoreCase)).ToList();
        return missing.Count == 0 ? "Every breaking change says what to do." : $"revise: {missing.Count} breaking note lacks advice.";
    }

    static ChatMessage? FactCheck(ModelCall call)
    {
        if (!call.Mentions("Check the release note against the change record"))
            return null;
        var result = call.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault();
        if (result is null)
        {
            var id = ChangeId().Match(call.User).Groups["id"].Value;
            return ToolCall("change_details", new Dictionary<string, object?> { ["id"] = id });
        }
        using var record = JsonDocument.Parse(result.Result?.ToString() ?? "{}");
        var title = record.RootElement.TryGetProperty("Title", out var t) ? t.GetString() ?? "" : "";
        var firstWord = title.Split(' ')[0];
        var note = call.User;
        var verdict = note.Contains(firstWord, StringComparison.OrdinalIgnoreCase) || title.Length == 0 ? "consistent" : "mismatch";
        return new ChatMessage(ChatRole.Assistant, $"Thought: I have read the record.\nfinal_answer: {verdict}");
    }

    static ChatMessage? Announce(ModelCall call)
    {
        if (!call.Mentions("You write short product announcements"))
            return null;
        var result = call.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault();
        if (result is null)
            return ToolCall("list_notes", new Dictionary<string, object?> { ["count"] = 3 });
        var lines = (result.Result?.ToString() ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var headline = Regex.Match(call.User, @"headline: (?<h>.*)").Groups["h"].Value.Trim();
        return new ChatMessage(ChatRole.Assistant,
            $"I read {lines.Length} notes. TASK_COMPLETE {headline}. Highlights: {string.Join(" ", lines.Select(l => l.Split(": ", 2)[^1]))}");
    }

    static ChatMessage ToolCall(string name, Dictionary<string, object?> arguments)
    {
        var message = new ChatMessage(ChatRole.Assistant, "");
        message.Contents.Add(new FunctionCallContent($"call-{name}", name, arguments));
        return message;
    }
}

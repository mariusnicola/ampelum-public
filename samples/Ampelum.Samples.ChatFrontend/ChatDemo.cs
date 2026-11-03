using System.Text;

namespace Ampelum.Samples.ChatFrontend;

/// <summary>Runs one scenario through every part of the backend and says whether each part did its job.</summary>
public static class ChatDemo
{
    /// <summary>
    /// Runs the scenario. <paramref name="failPrimary"/> makes the primary model fail on its next call;
    /// it is null when the model cannot be made to fail on demand, which skips the failover step.
    /// </summary>
    public static async Task<bool> RunAsync(ChatBackend backend, TextWriter output, Action? failPrimary, CancellationToken ct = default)
    {
        var ok = true;

        // 1. A conversation reply arrives token by token.
        var tokens = new List<string>();
        await foreach (var token in backend.OpenConversation().SendAsync("How long does delivery take?", ct))
            tokens.Add(token);
        output.WriteLine($"Stream:   {tokens.Count} tokens: {string.Join("|", tokens)}");
        ok &= tokens.Count > 1;

        // 2. The typed fields of an answer arrive as they are produced.
        var fieldsSeen = new List<string>();
        string? finalAnswer = null;
        await foreach (var update in backend.StreamFieldsAsync("What is the return policy?", ct))
        {
            if (!fieldsSeen.Contains(update.FieldName))
                fieldsSeen.Add(update.FieldName);
            if (update is { IsFinal: true, FinalValue: { } value })
                finalAnswer = $"{value.Answer} / {value.FollowUp}";
        }
        output.WriteLine($"Fields:   {string.Join(", ", fieldsSeen)}; final = {finalAnswer}");
        ok &= finalAnswer is not null && fieldsSeen.Count >= 2;

        // 3. The same question twice: the second answer comes from the semantic cache.
        var entriesBefore = backend.CachedAnswers;
        var first = await backend.AskAsync("Do you ship to Germany?", ct);
        var entriesAfterFirst = backend.CachedAnswers;
        var second = await backend.AskAsync("Do you ship to Germany?", ct);
        output.WriteLine($"Semantic: first = {first.Answer} | second = {second.Answer} | cached entries = {entriesBefore}, {entriesAfterFirst}, {backend.CachedAnswers}");
        ok &= first.Answer.Length > 0 && first.Answer == second.Answer
            && entriesAfterFirst == entriesBefore + 1 && backend.CachedAnswers == entriesAfterFirst;

        // 4. The same prompt twice: the second never reaches the model.
        var quick = await backend.QuickReplyAsync("What are your opening hours?", ct);
        var quickAgain = await backend.QuickReplyAsync("What are your opening hours?", ct);
        output.WriteLine($"Token:    first = {quick.Text} | again = {quickAgain.Text}");
        ok &= quick.Text.Length > 0 && quick.Text == quickAgain.Text;

        // 5. The primary model fails: the fallback model answers.
        if (failPrimary is null)
        {
            output.WriteLine("Failover: skipped, a live model cannot be made to fail on demand");
        }
        else
        {
            failPrimary();
            var rescued = await backend.QuickReplyAsync("Do gift cards expire?", ct);
            output.WriteLine($"Failover: served by {rescued.Endpoint}: {rescued.Text}");
            ok &= rescued.Endpoint == ChatBackend.FallbackEndpoint && rescued.Text.Length > 0;
        }

        output.WriteLine(ok ? "Result:   ok" : "Result:   FAILED");
        return ok;
    }
}

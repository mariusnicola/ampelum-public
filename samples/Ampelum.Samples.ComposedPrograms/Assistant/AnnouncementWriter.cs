using System.ComponentModel;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum;

namespace Ampelum.Samples.ComposedPrograms.Assistant;

/// <summary>
/// Hands the announcement to a task agent: it may read the notes through a tool, streams its answer and
/// ends by writing TASK_COMPLETE followed by the finished text.
/// </summary>
public sealed class AnnouncementWriter(AmpelumContext ctx)
{
    public const string SystemPrompt =
        "You write short product announcements. Read the release notes with the list_notes tool, then write the announcement. "
        + "End your last message with TASK_COMPLETE followed by the announcement text.";

    public async Task<AnnouncementResult> WriteAsync(
        string headline, IReadOnlyList<DraftedNote> notes, CancellationToken ct = default)
    {
        var listNotes = AIFunctionFactory.Create(
            ([Description("How many notes to return.")] int count) =>
                string.Join("\n", notes.Take(count).Select(n => $"{n.Kind}: {n.Note}")),
            "list_notes", "Returns the first release notes.");

        using var agent = new TaskAgent(ctx, SystemPrompt, [listNotes]);
        await agent.StartAsync($"Write the announcement for the release headline: {headline}", ct).ConfigureAwait(false);

        var streamed = new System.Text.StringBuilder();
        await foreach (var message in agent.Events.ReadAllAsync(ct).ConfigureAwait(false))
        {
            switch (message)
            {
                case TokenEvent token:
                    streamed.Append(token.Text);
                    break;
                case CompleteEvent complete:
                    return new AnnouncementResult(complete.Summary, streamed.ToString(), agent.Status);
                case ErrorEvent error:
                    throw new InvalidOperationException($"{error.Code}: {error.Message}");
            }
        }
        throw new InvalidOperationException("The announcement agent stopped without completing.");
    }
}

public sealed record AnnouncementResult(string Text, string Streamed, AgentStatus Status);

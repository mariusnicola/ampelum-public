using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SignalGrid.Ampelum.Context.Selection;

/// <summary>Default renderer that converts a selection plan into chat messages.</summary>
public sealed class DefaultContextRenderer : IContextRenderer
{
    /// <inheritdoc />
    public Task<IReadOnlyList<ChatMessage>> RenderAsync(SelectionPlan plan, ContextPrefix prefix, string mode, CancellationToken ct)
    {
        var messages = new List<ChatMessage>();

        messages.Add(new ChatMessage("system", FormatSystemMessage(prefix, mode)));

        foreach (var ev in plan.SelectedEvents)
        {
            var role = ev.Source switch
            {
                "user_msg" => "user",
                "assistant" or "tool_call" => "assistant",
                "tool_result" => "tool",
                _ => "user"
            };
            messages.Add(new ChatMessage(role, $"`{ev.EventKey}` — {ev.Body}"));
        }

        return Task.FromResult<IReadOnlyList<ChatMessage>>(messages);
    }

    private static string FormatSystemMessage(ContextPrefix prefix, string mode)
    {
        return $"[Mode: {mode}]\n{prefix.ModeBanner}\n{prefix.GoalAnchor}\n{prefix.ChapterIndex}";
    }
}

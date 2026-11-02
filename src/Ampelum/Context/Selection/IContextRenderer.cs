using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SignalGrid.Ampelum.Context.Selection;

/// <summary>Renders a selection plan into chat messages for the model.</summary>
public interface IContextRenderer
{
    /// <summary>Renders the selection plan into a list of chat messages.</summary>
    Task<IReadOnlyList<ChatMessage>> RenderAsync(SelectionPlan plan, ContextPrefix prefix, string mode, CancellationToken ct);
}

/// <summary>Simple chat message model for rendering.</summary>
public sealed record ChatMessage(string Role, string Content);

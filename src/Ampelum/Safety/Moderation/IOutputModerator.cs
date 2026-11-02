using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace SignalGrid.Ampelum.Safety.Moderation;

/// <summary>Async strategy for moderating an output payload against a content policy.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IOutputModerator
{
    /// <summary>Moderates <paramref name="text"/> and returns a structured verdict.</summary>
    ValueTask<ModerationResult> ModerateAsync(string text, CancellationToken cancellationToken);
}

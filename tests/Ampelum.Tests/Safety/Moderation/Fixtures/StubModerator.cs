using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SignalGrid.Ampelum.Safety.Moderation;

namespace Ampelum.Tests.Safety.Moderation.Fixtures;

internal sealed class StubModerator : IOutputModerator
{
    readonly ModerationResult _verdict;
    public int Calls;
    public string? LastInput;

    public StubModerator(bool flagged, params string[] categories)
        => _verdict = new ModerationResult(flagged, categories, Confidence: flagged ? 0.9 : 0.1);

    public ValueTask<ModerationResult> ModerateAsync(string text, CancellationToken cancellationToken)
    {
        Calls++;
        LastInput = text;
        return ValueTask.FromResult(_verdict);
    }
}

internal sealed class ThrowingModerator : IOutputModerator
{
    readonly System.Exception _ex;
    public ThrowingModerator(System.Exception ex) => _ex = ex;
    public ValueTask<ModerationResult> ModerateAsync(string text, CancellationToken cancellationToken)
        => throw _ex;
}

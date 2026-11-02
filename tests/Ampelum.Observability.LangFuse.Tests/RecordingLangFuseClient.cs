using System.Collections.Concurrent;
using SignalGrid.Ampelum.Observability.LangFuse;

namespace Ampelum.Observability.LangFuse.Tests;

internal sealed class RecordingLangFuseClient : ILangFuseClient
{
    public ConcurrentBag<IReadOnlyList<LangFuseEvent>> Submissions { get; } = new();
    public Func<IReadOnlyList<LangFuseEvent>, Task>? OnSubmit { get; set; }
    public int Count => Submissions.Count;
    public async Task SubmitAsync(IReadOnlyList<LangFuseEvent> events, CancellationToken cancellationToken)
    {
        Submissions.Add(events);
        if (OnSubmit is { } cb) await cb(events).ConfigureAwait(false);
    }
}

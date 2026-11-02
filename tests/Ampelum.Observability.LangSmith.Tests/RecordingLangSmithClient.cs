using Xunit;
using System.Collections.Concurrent;
using SignalGrid.Ampelum.Observability.LangSmith;

namespace Ampelum.Observability.LangSmith.Tests;

internal sealed class RecordingLangSmithClient : ILangSmithClient
{
    public ConcurrentBag<IReadOnlyList<LangSmithRun>> Submissions { get; } = new();
    public Func<IReadOnlyList<LangSmithRun>, Task>? OnSubmit { get; set; }
    public int Count => Submissions.Count;

    public async Task SubmitAsync(IReadOnlyList<LangSmithRun> runs, CancellationToken cancellationToken)
    {
        Submissions.Add(runs);
        if (OnSubmit is { } cb) await cb(runs).ConfigureAwait(false);
    }
}

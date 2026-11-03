namespace Ampelum.Samples.DocumentReview;

/// <summary>
/// A clock for the offline run: every timer fires at once, so a poll delay costs no time. It records
/// each delay it was asked for, which shows the poll loop waited through the clock it was given.
/// </summary>
internal sealed class InstantTimeProvider(DateTimeOffset now) : TimeProvider
{
    readonly List<TimeSpan> _delays = [];

    public IReadOnlyList<TimeSpan> Delays => _delays;

    public override DateTimeOffset GetUtcNow() => now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        _delays.Add(dueTime);
        // Queued instead of called inline so the timer object exists before its callback runs.
        ThreadPool.QueueUserWorkItem(_ => callback(state));
        return new SpentTimer();
    }

    sealed class SpentTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

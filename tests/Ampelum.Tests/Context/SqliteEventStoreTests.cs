using SignalGrid.Ampelum.Context;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Context;

public class SqliteEventStoreTests : IAsyncLifetime
{
    private string _dir = "";
    private SqliteEventStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sqlite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _store = await SqliteEventStore.OpenAsync(Path.Combine(_dir, "events.db"), TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public async Task Append_RoundTripsUserMessage()
    {
        await using var w = await _store.AcquireWriterLockAsync(TestContext.Current.CancellationToken);
        var seq = await _store.NextSeqAsync(0, TestContext.Current.CancellationToken);
        var key = EventKey.Make("user_msg", 0, seq, "hi");
        await _store.AppendAsync(new UserMessage(0, seq, DateTimeOffset.UtcNow, key, null, "hi"),
            TestContext.Current.CancellationToken);

        var list = new List<EventEntry>();
        await foreach (var e in _store.ReadAsync(new EventQuery(), TestContext.Current.CancellationToken)) list.Add(e);
        list.Count.ShouldBe(1);
        list[0].ShouldBeOfType<UserMessage>();
    }

    [Fact]
    public async Task TurnCounter_IncrementsOnUserMessageOnly()
    {
        await using var w = await _store.AcquireWriterLockAsync(TestContext.Current.CancellationToken);
        (await _store.CurrentTurnAsync(TestContext.Current.CancellationToken)).ShouldBe(0);

        var seq = await _store.NextSeqAsync(0, TestContext.Current.CancellationToken);
        await _store.AppendAsync(new UserMessage(0, seq, DateTimeOffset.UtcNow,
            EventKey.Make("user_msg", 0, seq, "a"), null, "a"),
            TestContext.Current.CancellationToken);
        (await _store.CurrentTurnAsync(TestContext.Current.CancellationToken)).ShouldBe(1);

        var seq2 = await _store.NextSeqAsync(1, TestContext.Current.CancellationToken);
        await _store.AppendAsync(new AssistantText(1, seq2, DateTimeOffset.UtcNow,
            EventKey.Make("assistant", 1, seq2, "b"), null, "b"),
            TestContext.Current.CancellationToken);
        (await _store.CurrentTurnAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task AcquireWriterLock_RejectsConcurrentHolders()
    {
        await using var w1 = await _store.AcquireWriterLockAsync(TestContext.Current.CancellationToken);
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await _store.AcquireWriterLockAsync(TestContext.Current.CancellationToken));
    }
}

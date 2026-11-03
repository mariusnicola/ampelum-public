using SignalGrid.Ampelum.Audit;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Audit;


public sealed class BatchedAuditSinkTests
{
    static AuditEntry MakeEntry(long seq) =>
        new()
        {
            Sequence = seq,
            Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(seq),
            ModuleName = "Predict",
            ModelId = "test-model",
            Inputs = new Dictionary<string, object?> { ["q"] = $"q{seq}" },
            Outputs = new Dictionary<string, object?> { ["a"] = $"a{seq}" },
            PromptTokens = 10,
            CompletionTokens = 5,
            PrevHash = new byte[32],
            EntryHash = new byte[32],
        };

    [Fact]
    public async Task AppendAsync_DrainsToInner_PreservingOrder()
    {
        var inner = new InMemoryAuditSink();
        var batched = new BatchedAuditSink(inner, capacity: 100);

        for (long i = 0; i < 25; i++)
        {
            await batched.AppendAsync(MakeEntry(i), TestContext.Current.CancellationToken);
        }
        await batched.DisposeAsync();

        var collected = new List<AuditEntry>();
        await foreach (var e in inner.EnumerateAsync(TestContext.Current.CancellationToken))
        {
            collected.Add(e);
        }
        collected.Count.ShouldBe(25);
        for (int i = 0; i < 25; i++) collected[i].Sequence.ShouldBe((long)i);
    }

    [Fact]
    public async Task AppendAsync_OnFullQueue_BlocksUntilSpaceAvailable()
    {
        // BlockingAuditSink holds the first inner.AppendAsync indefinitely. Drain reads entry 0
        // from the channel buffer (1/1), enters inner.AppendAsync, blocks. Channel buffer is now
        // empty — entry 1 fills the slot (1/1). Entry 2 must await space because both the in-flight
        // write to inner AND the buffered entry 1 are stalled.
        var inner = new BlockingAuditSink();
        var batched = new BatchedAuditSink(inner, capacity: 1);

        await batched.AppendAsync(MakeEntry(0), TestContext.Current.CancellationToken);
        await batched.AppendAsync(MakeEntry(1), TestContext.Current.CancellationToken);
        var blocked = batched.AppendAsync(MakeEntry(2), TestContext.Current.CancellationToken).AsTask();

        await Task.Delay(50, TestContext.Current.CancellationToken);
        blocked.IsCompleted.ShouldBeFalse();

        inner.Release();
        await blocked.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        blocked.IsCompletedSuccessfully.ShouldBeTrue();

        await batched.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_DrainsAllPendingEntriesBeforeReturning()
    {
        var inner = new InMemoryAuditSink();
        var batched = new BatchedAuditSink(inner, capacity: 200);
        for (long i = 0; i < 100; i++)
            await batched.AppendAsync(MakeEntry(i), TestContext.Current.CancellationToken);
        await batched.DisposeAsync();

        var n = 0;
        await foreach (var _ in inner.EnumerateAsync(TestContext.Current.CancellationToken))
            n++;
        n.ShouldBe(100);
    }

    [Fact]
    public async Task DisposeAsync_DisposesAnAsyncDisposableInner_AfterDraining()
    {
        var inner = new DisposableAuditSink();
        var batched = new BatchedAuditSink(inner);
        await batched.AppendAsync(MakeEntry(0), TestContext.Current.CancellationToken);

        await batched.DisposeAsync();

        inner.Appended.ShouldBe(1);
        inner.AsyncDisposed.ShouldBeTrue();
        inner.AppendedAfterDispose.ShouldBeFalse();
    }

    [Fact]
    public async Task DisposeAsync_DisposesASyncDisposableInner()
    {
        var inner = new SyncDisposableAuditSink();
        var batched = new BatchedAuditSink(inner);

        await batched.DisposeAsync();

        inner.Disposed.ShouldBeTrue();
    }

    sealed class DisposableAuditSink : IAuditSink, IAsyncDisposable
    {
        public int Appended;
        public bool AsyncDisposed;
        public bool AppendedAfterDispose;
        public long NextSequence => Appended;

        public ValueTask AppendAsync(AuditEntry entry, CancellationToken ct)
        {
            if (AsyncDisposed) AppendedAfterDispose = true;
            Appended++;
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<AuditEntry> EnumerateAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            AsyncDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    sealed class SyncDisposableAuditSink : IAuditSink, IDisposable
    {
        public bool Disposed;
        public long NextSequence => 0;
        public ValueTask AppendAsync(AuditEntry entry, CancellationToken ct) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<AuditEntry> EnumerateAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task NextSequence_AdvancesPerAppend()
    {
        var inner = new InMemoryAuditSink();
        var batched = new BatchedAuditSink(inner);
        batched.NextSequence.ShouldBe(0L);
        await batched.AppendAsync(MakeEntry(0), TestContext.Current.CancellationToken);
        batched.NextSequence.ShouldBe(1L);
        await batched.AppendAsync(MakeEntry(1), TestContext.Current.CancellationToken);
        batched.NextSequence.ShouldBe(2L);
        await batched.DisposeAsync();
    }

    [Fact]
    public async Task EnumerateAsync_DelegatesToInner()
    {
        var inner = new InMemoryAuditSink();
        var batched = new BatchedAuditSink(inner);
        await batched.AppendAsync(MakeEntry(0), TestContext.Current.CancellationToken);
        await batched.DisposeAsync();
        var n = 0;
        await foreach (var _ in batched.EnumerateAsync(TestContext.Current.CancellationToken))
            n++;
        n.ShouldBe(1);
    }

    [Fact]
    public void Ctor_NullInner_Throws() =>
        Should.Throw<ArgumentNullException>(() => new BatchedAuditSink(null!));

    [Fact]
    public void Ctor_ZeroCapacity_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => new BatchedAuditSink(new InMemoryAuditSink(), capacity: 0));

    [Fact]
    public async Task AppendAsync_NullEntry_Throws()
    {
        var batched = new BatchedAuditSink(new InMemoryAuditSink());
        await Should.ThrowAsync<ArgumentNullException>(async () =>
            await batched.AppendAsync(null!, TestContext.Current.CancellationToken));
        await batched.DisposeAsync();
    }

    [Fact]
    public async Task InnerThrows_DrainSwallows()
    {
        var inner = new ThrowingAuditSink();
        var batched = new BatchedAuditSink(inner, capacity: 5);
        await batched.AppendAsync(MakeEntry(0), TestContext.Current.CancellationToken);
        await batched.DisposeAsync();
        inner.Calls.ShouldBe(1);
    }

    sealed class BlockingAuditSink : IAuditSink
    {
        readonly ManualResetEventSlim _gate = new(false);
        long _nextSeq;
        public long NextSequence => Interlocked.Read(ref _nextSeq);
        public ValueTask AppendAsync(AuditEntry entry, CancellationToken ct)
        {
            _gate.Wait(ct);
            Interlocked.Increment(ref _nextSeq);
            return ValueTask.CompletedTask;
        }
        public async IAsyncEnumerable<AuditEntry> EnumerateAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
        public void Release() => _gate.Set();
    }

    sealed class ThrowingAuditSink : IAuditSink
    {
        public int Calls;
        public long NextSequence => 0;
        public ValueTask AppendAsync(AuditEntry entry, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("boom");
        }
        public async IAsyncEnumerable<AuditEntry> EnumerateAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}

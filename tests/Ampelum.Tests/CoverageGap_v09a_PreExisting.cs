using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.DependencyInjection;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.CoverageGapPreExisting;

public class PreExistingGapTests
{
    sealed class ThrowingTraceSink : ITraceSink
    {
        public void Write(TraceRecord record) => throw new InvalidOperationException("boom");
    }

    static TraceRecord SampleTrace() => new(
        Guid.NewGuid(), "m", typeof(string),
        new Dictionary<string, object?>(),
        new Dictionary<string, object?>(),
        [], "", null, TimeSpan.Zero, null,
        new Dictionary<string, object?>());

    [Fact]
    public async Task BatchedTraceSink_dispose_async_swallows_inner_exception()
    {
        var sink = new BatchedTraceSink(new ThrowingTraceSink(), capacity: 4);
        sink.Write(SampleTrace());
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await sink.DisposeAsync();
    }

    [Fact]
    public void BatchedTraceSink_dispose_swallows_inner_exception()
    {
        var sink = new BatchedTraceSink(new ThrowingTraceSink(), capacity: 4);
        sink.Write(SampleTrace());
        Thread.Sleep(50);
        sink.Dispose();
    }

    sealed class ThrowingAuditSink : IAuditSink
    {
        public long NextSequence => 0;
        public ValueTask AppendAsync(AuditEntry entry, CancellationToken ct) =>
            throw new InvalidOperationException("audit boom");
        public async IAsyncEnumerable<AuditEntry> EnumerateAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield break;
        }
    }

    static AuditEntry SampleAuditEntry() => new()
    {
        Sequence = 0,
        Timestamp = DateTimeOffset.UtcNow,
        ModelId = "test",
        ModuleName = "m",
        Inputs = new Dictionary<string, object?>(),
        Outputs = new Dictionary<string, object?>(),
        PromptTokens = 0,
        CompletionTokens = 0,
        PrevHash = Array.Empty<byte>(),
        EntryHash = Array.Empty<byte>(),
    };

    [Fact]
    public async Task BatchedAuditSink_dispose_async_swallows_inner_exception()
    {
        var inner = new ThrowingAuditSink();
        var sink = new BatchedAuditSink(inner, capacity: 4);
        await sink.AppendAsync(SampleAuditEntry(), TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await sink.DisposeAsync();
    }

    sealed class TestAuditSink : IAuditSink
    {
        public long NextSequence => 0;
        public ValueTask AppendAsync(AuditEntry entry, CancellationToken ct) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<AuditEntry> EnumerateAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield break;
        }
    }

    [Fact]
    public void AddBatchedAuditSink_registers_wrapper()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(new FakeChatClient());
        services.AddAmpelum().AddBatchedAuditSink<TestAuditSink>(capacity: 8);
        var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService<IAuditSink>();
        resolved.ShouldBeOfType<BatchedAuditSink>();
    }
}

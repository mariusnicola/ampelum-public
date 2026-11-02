using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Adapters;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Deadline;

public sealed class AmpelumContextDeadlineTests
{
    static AmpelumContext NewCtx() => new(
        Chat: new StubChat(),
        Adapter: new ChatAdapter(),
        TraceSink: NullTraceSink.Instance,
        LoggerFactory: NullLoggerFactory.Instance);

    sealed class StubChat : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("stub");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse());
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public void Deadline_DefaultsToNull()
    {
        NewCtx().Deadline.ShouldBeNull();
    }

    [Fact]
    public void WithDeadline_SetsDeadlineToNowPlusDuration()
    {
        var before = DateTimeOffset.UtcNow;
        var ctx = NewCtx().WithDeadline(TimeSpan.FromSeconds(30));
        ctx.Deadline.ShouldNotBeNull();
        ctx.Deadline!.Value.ShouldBeGreaterThanOrEqualTo(before + TimeSpan.FromSeconds(29));
    }

    [Fact]
    public void WithDeadline_NegativeOrZero_Throws()
    {
        var ctx = NewCtx();
        Should.Throw<ArgumentOutOfRangeException>(() => ctx.WithDeadline(TimeSpan.Zero));
        Should.Throw<ArgumentOutOfRangeException>(() => ctx.WithDeadline(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void WithDeadline_DoesNotMutateOriginal()
    {
        var original = NewCtx();
        var derived = original.WithDeadline(TimeSpan.FromSeconds(5));
        original.Deadline.ShouldBeNull();
        derived.Deadline.ShouldNotBeNull();
    }
}

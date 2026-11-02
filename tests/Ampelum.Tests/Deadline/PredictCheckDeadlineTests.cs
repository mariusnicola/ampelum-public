using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Adapters;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Hooks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Deadline;

public sealed class PredictCheckDeadlineTests
{
    sealed class StubChat : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("stub");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) => Task.FromResult(new ChatResponse());
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) => throw new NotSupportedException();
        public object? GetService(Type s, object? k = null) => null;
        public void Dispose() { }
    }

    static AmpelumContext Ctx(DateTimeOffset? deadline = null) => new(
        new StubChat(), new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance) { Deadline = deadline };

    static HookContext NewHookCtx() => new()
    {
        ModuleName = "test",
        Inputs = new Dictionary<string, object?>(),
    };

    [Fact]
    public void NullDeadline_NoHook_NoThrow()
    {
        Predict<TestSig>.CheckDeadline(Ctx(), null, "start");
    }

    [Fact]
    public void FutureDeadline_NoThrow()
    {
        Predict<TestSig>.CheckDeadline(Ctx(DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5)), null, "start");
    }

    [Fact]
    public void PastDeadline_OnContext_Throws()
    {
        var d = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1);
        var ex = Should.Throw<DeadlineExceededException>(
            () => Predict<TestSig>.CheckDeadline(Ctx(d), null, "post-lm"));
        ex.Phase.ShouldBe("post-lm");
        ex.Deadline.ShouldBe(d);
    }

    [Fact]
    public void PastDeadline_OnHookMetadata_Throws()
    {
        var hookCtx = NewHookCtx();
        var d = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1);
        hookCtx.Set(HookMetadataKeys.DeadlineUtc, d);
        Should.Throw<DeadlineExceededException>(
            () => Predict<TestSig>.CheckDeadline(Ctx(), hookCtx, "start"));
    }

    [Fact]
    public void EarlierDeadlineWins_ContextEarlier()
    {
        // Context says 1s ago, hook says +1h. Earlier is context (past) â€” throws.
        var hookCtx = NewHookCtx();
        hookCtx.Set(HookMetadataKeys.DeadlineUtc, DateTimeOffset.UtcNow + TimeSpan.FromHours(1));
        Should.Throw<DeadlineExceededException>(
            () => Predict<TestSig>.CheckDeadline(Ctx(DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1)), hookCtx, "x"));
    }

    [Fact]
    public void EarlierDeadlineWins_HookEarlier()
    {
        // Context says +1h, hook says past. Earlier is hook (past) â€” throws.
        var hookCtx = NewHookCtx();
        hookCtx.Set(HookMetadataKeys.DeadlineUtc, DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1));
        Should.Throw<DeadlineExceededException>(
            () => Predict<TestSig>.CheckDeadline(Ctx(DateTimeOffset.UtcNow + TimeSpan.FromHours(1)), hookCtx, "x"));
    }

    [Fact]
    public void HookMetadata_NonDateTimeOffset_Ignored()
    {
        var hookCtx = NewHookCtx();
        hookCtx.Set(HookMetadataKeys.DeadlineUtc, "not-a-date");
        // Context deadline far future; non-DTO metadata ignored â€” no throw.
        Predict<TestSig>.CheckDeadline(Ctx(DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5)), hookCtx, "x");
    }

    [Signature("Test")]
    public sealed record TestSig : ISignature
    {
        [Input] public required string Q { get; init; }
        [Output] public string A { get; init; } = "";
    }
}

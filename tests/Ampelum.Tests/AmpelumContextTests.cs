using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Hooks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

public sealed class AmpelumContextTests
{
    [Fact]
    public void Ctor_SetsAllFields()
    {
        var chat = new StubChat();
        var adapter = new StubAdapter();
        var sink = new InMemoryTraceSink();
        var ctx = new AmpelumContext(chat, adapter, sink, NullLoggerFactory.Instance);
        ctx.Chat.ShouldBeSameAs(chat);
        ctx.Adapter.ShouldBeSameAs(adapter);
        ctx.TraceSink.ShouldBeSameAs(sink);
        ctx.LoggerFactory.ShouldBeSameAs(NullLoggerFactory.Instance);
        ctx.DefaultChatOptions.ShouldBeNull();
    }

    [Fact]
    public void DefaultChatOptions_RoundTrips()
    {
        var opts = new ChatOptions();
        var ctx = new AmpelumContext(new StubChat(), new StubAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance, opts);
        ctx.DefaultChatOptions.ShouldBeSameAs(opts);
    }

    static AmpelumContext MakeCtx() =>
        new(new StubChat(), new StubAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);

    sealed class StubHookA : IHook
    {
        public int Order => 0;
        public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct) => ValueTask.CompletedTask;
    }

    sealed class StubHookB : IHook
    {
        public int Order => 1;
        public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct) => ValueTask.CompletedTask;
    }

    [Fact]
    public void WithoutHook_NullHooks_ReturnsSameInstance()
    {
        var ctx = MakeCtx();
        ctx.WithoutHook<StubHookA>().ShouldBeSameAs(ctx);
    }

    [Fact]
    public void WithoutHook_TypeNotPresent_ReturnsSameInstance()
    {
        var hookB = new StubHookB();
        var ctx = MakeCtx().WithHook(hookB);
        ctx.WithoutHook<StubHookA>().ShouldBeSameAs(ctx);
    }

    [Fact]
    public void WithoutHook_RemovesMatchingType_LeavesOthers()
    {
        var hookA = new StubHookA();
        var hookB = new StubHookB();
        var ctx = MakeCtx().WithHook(hookA).WithHook(hookB);
        var result = ctx.WithoutHook<StubHookA>();
        result.Hooks!.Count.ShouldBe(1);
        result.Hooks![0].ShouldBeSameAs(hookB);
    }

    [Fact]
    public void WithoutHook_RemovesOnlyHook_SetsHooksNull()
    {
        var ctx = MakeCtx().WithHook(new StubHookA());
        var result = ctx.WithoutHook<StubHookA>();
        result.Hooks.ShouldBeNull();
    }

    [Fact]
    public void WithoutHook_RemovesAllOfType_WhenMultipleSameType()
    {
        var a1 = new StubHookA();
        var a2 = new StubHookA();
        var ctx = MakeCtx().WithHook(a1).WithHook(a2);
        var result = ctx.WithoutHook<StubHookA>();
        result.Hooks.ShouldBeNull();
    }

    sealed class StubChat : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("stub", null, null);
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) => throw new NotImplementedException();
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    sealed class StubAdapter : IAdapter
    {
        public IReadOnlyList<ChatMessage> Format(SignatureMetadata s, IReadOnlyDictionary<string, object?> i, IReadOnlyList<Example> d) => [];
        public IReadOnlyDictionary<string, object?> Parse(SignatureMetadata s, ChatResponse r) => new Dictionary<string, object?>();
    }
}

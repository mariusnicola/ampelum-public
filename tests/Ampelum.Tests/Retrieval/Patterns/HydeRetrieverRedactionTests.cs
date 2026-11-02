using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Retrieval.Patterns;
using SignalGrid.Ampelum.Safety.Pii;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

public sealed class HydeRetrieverRedactionTests
{
    sealed class CapturingRetriever : IRetriever
    {
        public string? LastQuery;
        public Task<IReadOnlyList<RetrievedDocument>> RetrieveAsync(
            string query, int topK, IReadOnlyDictionary<string, object?>? filters = null, CancellationToken ct = default)
        {
            LastQuery = query;
            return Task.FromResult<IReadOnlyList<RetrievedDocument>>(new[]
            {
                new RetrievedDocument("doc1", "x", 1.0, new Dictionary<string, object?>()),
            });
        }
    }

    sealed class FakeRedactor : IPiiRedactor
    {
        public int Calls;
        public string Redact(string text) { Calls++; return text.Replace("SECRET", "[REDACTED]"); }
        public IReadOnlyDictionary<string, object?> RedactDictionary(IReadOnlyDictionary<string, object?> source) =>
            source.ToDictionary(kv => kv.Key, kv => kv.Value is string s ? (object?)Redact(s) : kv.Value);
    }

    [Fact]
    public async Task NoRedactor_Behaves_As_Before_Hashes_Plain()
    {
        var inner = new CapturingRetriever();
        var chat = new FakeChatClient().EnqueueText("the SECRET document");
        var hyde = new HydeRetriever(inner, chat, new HydeOptions());
        await hyde.RetrieveAsync("q", 5, ct: TestContext.Current.CancellationToken);
        inner.LastQuery.ShouldBe("the SECRET document");
    }

    [Fact]
    public async Task Redactor_Called_Once_Per_Retrieve()
    {
        var inner = new CapturingRetriever();
        var chat = new FakeChatClient().EnqueueText("hello SECRET world");
        var redactor = new FakeRedactor();
        var hyde = new HydeRetriever(inner, chat, new HydeOptions(), redactor);
        await hyde.RetrieveAsync("q", 5, ct: TestContext.Current.CancellationToken);
        redactor.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Redactor_Output_Forwarded_To_Inner()
    {
        var inner = new CapturingRetriever();
        var chat = new FakeChatClient().EnqueueText("hello SECRET world");
        var hyde = new HydeRetriever(inner, chat, new HydeOptions(), new FakeRedactor());
        await hyde.RetrieveAsync("q", 5, ct: TestContext.Current.CancellationToken);
        inner.LastQuery.ShouldBe("hello [REDACTED] world");
    }

    [Fact]
    public async Task Disabled_Hyde_Skips_Redactor()
    {
        var inner = new CapturingRetriever();
        var chat = new FakeChatClient();
        var redactor = new FakeRedactor();
        var hyde = new HydeRetriever(inner, chat, new HydeOptions { IsEnabled = false }, redactor);
        await hyde.RetrieveAsync("q", 5, ct: TestContext.Current.CancellationToken);
        redactor.Calls.ShouldBe(0);
        inner.LastQuery.ShouldBe("q");
    }

    [Fact]
    public void Ctor_Null_Args_Throw()
    {
        Should.Throw<ArgumentNullException>(() => new HydeRetriever(null!, new FakeChatClient(), new HydeOptions()));
        Should.Throw<ArgumentNullException>(() => new HydeRetriever(new CapturingRetriever(), null!, new HydeOptions()));
        Should.Throw<ArgumentNullException>(() => new HydeRetriever(new CapturingRetriever(), new FakeChatClient(), null!));
    }

    sealed class SpyHook : IHook
    {
        public int Order => 0;
        public List<string> Calls { get; } = [];
        public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct)
        {
            Calls.Add($"before:{ctx.ModuleName}");
            return default;
        }
        public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct)
        {
            Calls.Add($"after:{ctx.ModuleName}");
            return default;
        }
        public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct) => default;
    }

    [Fact]
    public async Task NoAmbientContext_CallsInjectedChatDirectly()
    {
        var inner = new CapturingRetriever();
        var chat = new FakeChatClient().EnqueueText("hyde answer");
        var hyde = new HydeRetriever(inner, chat, new HydeOptions());

        await hyde.RetrieveAsync("q", 5, ct: TestContext.Current.CancellationToken);

        inner.LastQuery.ShouldBe("hyde answer");
    }

    [Fact]
    public async Task AmbientContext_RunsHooks_ButUsesInjectedChatClient()
    {
        var inner = new CapturingRetriever();
        var chat = new FakeChatClient().EnqueueText("from injected client");
        var decoyChat = new FakeChatClient().EnqueueText("from decoy client");
        var hook = new SpyHook();
        var ambient = new AmpelumContext(decoyChat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
        {
            Hooks = [hook],
        };
        var hyde = new HydeRetriever(inner, chat, new HydeOptions());

        using (AmpelumContext.Push(ambient))
        {
            await hyde.RetrieveAsync("q", 5, ct: TestContext.Current.CancellationToken);
        }

        inner.LastQuery.ShouldBe("from injected client");
        hook.Calls.ShouldBe(["before:HydeRetriever.GenerateHypothesis", "after:HydeRetriever.GenerateHypothesis"]);
        decoyChat.RecordedCalls.ShouldBeEmpty();
    }
}

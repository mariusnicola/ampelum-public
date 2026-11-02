using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

public sealed class FakeChatClientStreamingTests
{
    // ── Single-output signature (baseline) ──────────────────────────────────

    [Signature("")] public sealed record S : ISignature
    {
        [Input]  public required string Q { get; init; }
        [Output] public string A { get; init; } = "";
    }

    // ── Two-output signature (exercises inter-marker split path) ─────────────

    [Signature("")] public sealed record TwoOut : ISignature
    {
        [Input]  public required string Q { get; init; }
        [Output] public string First  { get; init; } = "";
        [Output] public string Second { get; init; } = "";
    }

    // ── Core happy-path tests (as specified) ─────────────────────────────────

    [Fact]
    public async Task EnqueueStreamingChunks_YieldsOnePerChunk()
    {
        var chat = new FakeChatClient().EnqueueStreamingChunks("foo", "bar");
        var collected = new List<string>();
        await foreach (var u in chat.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "x")],
            cancellationToken: TestContext.Current.CancellationToken))
            collected.Add(u.Text ?? "");
        collected.ShouldBe(["foo", "bar"]);
    }

    [Fact]
    public async Task EnqueueStreamingOutputs_ProducesParsableChunks()
    {
        var chat = new FakeChatClient().EnqueueStreamingOutputs(new S { Q = "?", A = "Paris" });
        var total = "";
        await foreach (var u in chat.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "x")],
            cancellationToken: TestContext.Current.CancellationToken))
            total += u.Text ?? "";
        total.ShouldContain("[[ ## A ## ]]");
        total.ShouldContain("Paris");
    }

    [Fact]
    public async Task EnqueueStreamingChunks_WithCancelledToken_ThrowsOperationCanceled()
    {
        var chat = new FakeChatClient().EnqueueStreamingChunks("a", "b");
#pragma warning disable xUnit1051 // deliberately using a pre-cancelled token, not test context token
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in chat.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "x")], cancellationToken: cts.Token))
            { }
        });
#pragma warning restore xUnit1051
    }

    // ── Branch / throw-path coverage ─────────────────────────────────────────

    [Fact]
    public void EnqueueStreamingChunks_NullChunks_Throws() =>
        Should.Throw<ArgumentNullException>(() => new FakeChatClient().EnqueueStreamingChunks(null!));

    [Fact]
    public void EnqueueStreamingOutputs_NullOutputs_Throws() =>
        Should.Throw<ArgumentNullException>(() => new FakeChatClient().EnqueueStreamingOutputs<S>(null!));

    [Fact]
    public void EnqueueStreamingOutputs_NonTestableAdapter_Throws()
    {
        var bad = new BareAdapter();
        Should.Throw<InvalidOperationException>(
            () => new FakeChatClient().EnqueueStreamingOutputs(new S { Q = "?", A = "x" }, bad));
    }

    [Fact]
    public async Task GetStreamingResponseAsync_EmptyQueue_Throws()
    {
        var chat = new FakeChatClient();
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in chat.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "x")],
                cancellationToken: TestContext.Current.CancellationToken)) { }
        });
    }

    // ── Two-output: inter-marker split and RecordedCalls ─────────────────────

    [Fact]
    public async Task EnqueueStreamingOutputs_TwoOutputs_BothFieldsPresent()
    {
        var chat = new FakeChatClient().EnqueueStreamingOutputs(
            new TwoOut { Q = "?", First = "Alpha", Second = "Beta" });

        var total = "";
        await foreach (var u in chat.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "x")],
            cancellationToken: TestContext.Current.CancellationToken))
            total += u.Text ?? "";

        total.ShouldContain("[[ ## First ## ]]");
        total.ShouldContain("Alpha");
        total.ShouldContain("[[ ## Second ## ]]");
        total.ShouldContain("Beta");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_RecordsCall()
    {
        var chat = new FakeChatClient().EnqueueStreamingChunks("hi");
        await foreach (var _ in chat.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "x")],
            cancellationToken: TestContext.Current.CancellationToken)) { }
        chat.RecordedCalls.Count.ShouldBe(1);
        chat.RecordedCalls[0].Messages[0].Text.ShouldBe("x");
    }

    // ── Empty-wire adapter: covers the if (idx < 0) continue branch ─────────

    [Fact]
    public async Task EnqueueStreamingOutputs_EmptyWireResponse_ProducesNoChunks()
    {
        // EmptyWireTestableAdapter.FormatResponse returns a ChatResponse whose .Text
        // is "" (empty), so IndexOf for every field marker returns -1, exercising
        // the `if (idx < 0) continue` guard.
        var chat = new FakeChatClient().EnqueueStreamingOutputs(new S { Q = "?", A = "" }, new EmptyWireTestableAdapter());
        var total = "";
        await foreach (var u in chat.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "x")],
            cancellationToken: TestContext.Current.CancellationToken))
            total += u.Text ?? "";
        // wire is "" so no chunks are produced — just verify no exception is thrown
        total.ShouldBe("");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    sealed class BareAdapter : IAdapter
    {
        public IReadOnlyList<ChatMessage> Format(SignatureMetadata s, IReadOnlyDictionary<string, object?> i, IReadOnlyList<Example> d) => [];
        public IReadOnlyDictionary<string, object?> Parse(SignatureMetadata s, ChatResponse r) => new Dictionary<string, object?>();
    }

    /// <summary>Returns a <see cref="ChatResponse"/> whose <c>.Text</c> is <c>""</c> (empty).</summary>
    sealed class EmptyWireTestableAdapter : IAdapter, ITestableAdapter
    {
        public IReadOnlyList<ChatMessage> Format(SignatureMetadata s, IReadOnlyDictionary<string, object?> i, IReadOnlyList<Example> d) => [];
        public IReadOnlyDictionary<string, object?> Parse(SignatureMetadata s, ChatResponse r) => new Dictionary<string, object?>();
        public ChatResponse FormatResponse(SignatureMetadata s, IReadOnlyDictionary<string, object?> o)
        {
            // A message with no content parts → ChatResponse.Text is "" (not null),
            // which means wire = "" and every IndexOf returns -1.
            var msg = new ChatMessage(ChatRole.Assistant, []);
            return new ChatResponse(msg);
        }
    }
}

using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

public sealed class FakeChatClientTests
{
    [Signature("")]
    sealed record Qa : ISignature
    {
        [Input]  public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    [Fact]
    public async Task EnqueueText_ReturnsAssistantMessage()
    {
        var fake = new FakeChatClient().EnqueueText("hi");
        var r = await fake.GetResponseAsync([new ChatMessage(ChatRole.User, "?")], cancellationToken: TestContext.Current.CancellationToken);
        r.Text.ShouldBe("hi");
    }

    [Fact]
    public async Task EnqueueOutputs_RoundTripsThroughAdapter()
    {
        var fake = new FakeChatClient().EnqueueOutputs<Qa>(new Qa { Question = "?", Answer = "Paris" });
        var predict = Predict.Create<Qa>(fake);
        var r = await predict.ForwardAsync(new Qa { Question = "?" }, ct: TestContext.Current.CancellationToken);
        r.Value.Answer.ShouldBe("Paris");
    }

    [Fact]
    public void EnqueueOutputs_NonTestableAdapter_Throws()
    {
        var bad = new BareAdapter();
        var fake = new FakeChatClient();
        Should.Throw<InvalidOperationException>(() => fake.EnqueueOutputs<Qa>(new Qa { Question = "?", Answer = "x" }, bad));
    }

    [Fact]
    public async Task EnqueueError_Throws()
    {
        var fake = new FakeChatClient().EnqueueError(new InvalidOperationException("boom"));
        await Should.ThrowAsync<InvalidOperationException>(() =>
            fake.GetResponseAsync([new ChatMessage(ChatRole.User, "?")], cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EmptyQueue_Throws()
    {
        var fake = new FakeChatClient();
        await Should.ThrowAsync<InvalidOperationException>(() =>
            fake.GetResponseAsync([new ChatMessage(ChatRole.User, "?")], cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RecordedCalls_Captured()
    {
        var fake = new FakeChatClient().EnqueueText("x").EnqueueText("y");
        await fake.GetResponseAsync([new ChatMessage(ChatRole.User, "?")], cancellationToken: TestContext.Current.CancellationToken);
        await fake.GetResponseAsync([new ChatMessage(ChatRole.User, "??")], cancellationToken: TestContext.Current.CancellationToken);
        fake.RecordedCalls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task RecordedChatCall_ExposesMessagesAndOptions()
    {
        var opts = new ChatOptions { ModelId = "test-model" };
        var fake = new FakeChatClient().EnqueueText("z");
        var msg = new ChatMessage(ChatRole.User, "hello");
        await fake.GetResponseAsync([msg], options: opts, cancellationToken: TestContext.Current.CancellationToken);
        var call = fake.RecordedCalls[0];
        call.Messages.Count.ShouldBe(1);
        call.Messages[0].Text.ShouldBe("hello");
        call.Options.ShouldNotBeNull();
        call.Options!.ModelId.ShouldBe("test-model");
    }

    [Fact]
    public void Dispose_IsNoOp() => new FakeChatClient().Dispose();

    [Fact]
    public void GetService_ReturnsNull() =>
        new FakeChatClient().GetService(typeof(string)).ShouldBeNull();

    [Fact]
    public async Task EnqueueText_WithUsage_ResponseHasUsage()
    {
        var usage = new UsageDetails { InputTokenCount = 4, OutputTokenCount = 2 };
        var fake = new FakeChatClient().EnqueueText("hello", usage);
        var r = await fake.GetResponseAsync([new ChatMessage(ChatRole.User, "?")], cancellationToken: TestContext.Current.CancellationToken);
        r.Text.ShouldBe("hello");
        r.Usage.ShouldNotBeNull();
        r.Usage!.InputTokenCount.ShouldBe(4);
        r.Usage!.OutputTokenCount.ShouldBe(2);
    }

    [Fact]
    public async Task EnqueueText_WithNullUsage_ResponseHasNullUsage()
    {
        var fake = new FakeChatClient().EnqueueText("hi", usage: null);
        var r = await fake.GetResponseAsync([new ChatMessage(ChatRole.User, "?")], cancellationToken: TestContext.Current.CancellationToken);
        r.Text.ShouldBe("hi");
        r.Usage.ShouldBeNull();
    }

    [Fact]
    public async Task EnqueueText_WithUsageModelAndResponseId_ResponseCarriesAll()
    {
        var usage = new UsageDetails { InputTokenCount = 11, OutputTokenCount = 22 };
        var fake = new FakeChatClient().EnqueueText("hello", usage, modelId: "test-model", responseId: "resp-xyz");
        var r = await fake.GetResponseAsync([new ChatMessage(ChatRole.User, "?")], cancellationToken: TestContext.Current.CancellationToken);
        r.Text.ShouldBe("hello");
        r.Usage!.InputTokenCount.ShouldBe(11);
        r.Usage!.OutputTokenCount.ShouldBe(22);
        r.ModelId.ShouldBe("test-model");
        r.ResponseId.ShouldBe("resp-xyz");
    }

    [Fact]
    public void EnqueueOutputs_NullOutputs_Throws() =>
        Should.Throw<ArgumentNullException>(() => new FakeChatClient().EnqueueOutputs<Qa>(null!));

    [Fact]
    public async Task EnqueueRaw_ReturnsExactResponse()
    {
        var expected = new ChatResponse(new ChatMessage(ChatRole.Assistant, "raw"));
        var fake = new FakeChatClient().EnqueueRaw(expected);
        var r = await fake.GetResponseAsync([], cancellationToken: TestContext.Current.CancellationToken);
        r.ShouldBeSameAs(expected);
    }

    sealed class BareAdapter : IAdapter
    {
        public IReadOnlyList<ChatMessage> Format(SignatureMetadata s, IReadOnlyDictionary<string, object?> i, IReadOnlyList<Example> d) => [];
        public IReadOnlyDictionary<string, object?> Parse(SignatureMetadata s, ChatResponse r) => new Dictionary<string, object?>();
    }
}

public sealed class CapturingTraceSinkTests
{
    static TraceRecord MakeRecord(string name) =>
        new(
            Id: Guid.NewGuid(),
            ModuleName: name,
            SignatureType: typeof(CapturingTraceSinkTests),
            Inputs: new Dictionary<string, object?>(),
            Outputs: new Dictionary<string, object?>(),
            PromptMessages: Array.Empty<ChatMessage>(),
            RawResponse: string.Empty,
            Usage: null,
            Duration: TimeSpan.Zero,
            Error: null,
            Metadata: new Dictionary<string, object?>());

    [Fact]
    public void Write_NullRecord_Throws() =>
        Should.Throw<ArgumentNullException>(() => new CapturingTraceSink().Write(null!));

    [Fact]
    public void Write_AppendsInOrder()
    {
        var sink = new CapturingTraceSink();
        sink.Write(MakeRecord("a"));
        sink.Write(MakeRecord("b"));
        sink.Write(MakeRecord("c"));
        sink.Records.Select(r => r.ModuleName).ShouldBe(["a", "b", "c"]);
    }

    [Fact]
    public void Clear_RemovesAllRecords()
    {
        var sink = new CapturingTraceSink();
        sink.Write(MakeRecord("a"));
        sink.Write(MakeRecord("b"));
        sink.Clear();
        sink.Records.Count.ShouldBe(0);
    }

    [Fact]
    public void GetRecent_ReturnsLastN()
    {
        var sink = new CapturingTraceSink();
        sink.Write(MakeRecord("a"));
        sink.Write(MakeRecord("b"));
        sink.Write(MakeRecord("c"));
        sink.GetRecent(2).Select(r => r.ModuleName).ShouldBe(["b", "c"]);
    }

    [Fact]
    public void GetRecent_NLargerThanCount_ReturnsAll()
    {
        var sink = new CapturingTraceSink();
        sink.Write(MakeRecord("a"));
        sink.GetRecent(5).Count.ShouldBe(1);
    }

    [Fact]
    public void GetRecent_Zero_ReturnsEmpty()
    {
        var sink = new CapturingTraceSink();
        sink.Write(MakeRecord("a"));
        sink.GetRecent(0).Count.ShouldBe(0);
    }
}

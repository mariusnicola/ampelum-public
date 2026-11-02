using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Adapters;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

public sealed class TwoStepAdapterTests
{
    [Signature("Answer questions.")]
    sealed record QA : ISignature
    {
        [Input] public string Question { get; init; } = "";
        [Output] public string Answer { get; init; } = "";
    }

    // Multi-type signature for ConvertJson branch coverage.
    [Signature("")]
    sealed record MultiType : ISignature
    {
        [Input]  public string Text { get; init; } = "";
        [Output] public int    Count   { get; init; }
        [Output] public long   BigNum  { get; init; }
        [Output] public double Score   { get; init; }
        [Output] public bool   Valid   { get; init; }
        [Output] public float  Weight  { get; init; }   // triggers GetRawText fallback
    }

    static readonly SignatureMetadata Meta      = SignatureMetadataCache.For<QA>();
    static readonly SignatureMetadata MetaMulti = SignatureMetadataCache.For<MultiType>();

    // ── Spec tests (plan Task 21, lines 3950-3978) ──────────────────────────

    [Fact]
    public void Format_MirrorsChatAdapter()
    {
        var extract = new FakeChatClient();
        var msgs = new TwoStepAdapter(extract).Format(Meta,
            new Dictionary<string, object?> { ["Question"] = "?" }, Array.Empty<Example>());
        msgs.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Parse_SecondCallExtractsJson()
    {
        var extract = new FakeChatClient().EnqueueText("{\"Answer\": \"paris\"}");
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant,
            "The capital of france is paris because history."));
        new TwoStepAdapter(extract).Parse(Meta, response)["Answer"].ShouldBe("paris");
    }

    [Fact]
    public void Parse_ExtractionFails_Throws()
    {
        var extract = new FakeChatClient().EnqueueText("not json");
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "raw text"));
        Should.Throw<AdapterParseException>(() =>
            new TwoStepAdapter(extract).Parse(Meta, response));
    }

    // ── Coverage extras ──────────────────────────────────────────────────────

    [Fact]
    public void Ctor_NullExtractionClient_Throws()
    {
        Should.Throw<ArgumentNullException>(() => new TwoStepAdapter(null!));
    }

    [Fact]
    public void Parse_ExtractionClientThrows_ThrowsAdapterParseException()
    {
        var extract = new FakeChatClient().EnqueueError(new InvalidOperationException("extraction failed"));
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "raw text"));
        var ex = Should.Throw<AdapterParseException>(() =>
            new TwoStepAdapter(extract).Parse(Meta, response));
        ex.Hint.ShouldContain("extraction call failed");
    }

    [Fact]
    public void ConvertJson_String_ReturnsString()
    {
        var extract = new FakeChatClient().EnqueueText("{\"Answer\": \"test value\"}");
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "raw"));
        var result = new TwoStepAdapter(extract).Parse(Meta, response);
        result["Answer"].ShouldBe("test value");
    }

    [Fact]
    public void ConvertJson_Int_ReturnsInt()
    {
        var extract = new FakeChatClient().EnqueueText("{\"Count\": 42, \"BigNum\": 0, \"Score\": 0, \"Valid\": false, \"Weight\": 0}");
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "raw"));
        var result = new TwoStepAdapter(extract).Parse(MetaMulti, response);
        result["Count"].ShouldBe(42);
    }

    [Fact]
    public void ConvertJson_Long_ReturnsLong()
    {
        var extract = new FakeChatClient().EnqueueText("{\"Count\": 0, \"BigNum\": 9876543210, \"Score\": 0, \"Valid\": false, \"Weight\": 0}");
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "raw"));
        var result = new TwoStepAdapter(extract).Parse(MetaMulti, response);
        result["BigNum"].ShouldBe(9876543210L);
    }

    [Fact]
    public void ConvertJson_Double_ReturnsDouble()
    {
        var extract = new FakeChatClient().EnqueueText("{\"Count\": 0, \"BigNum\": 0, \"Score\": 3.14, \"Valid\": false, \"Weight\": 0}");
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "raw"));
        var result = new TwoStepAdapter(extract).Parse(MetaMulti, response);
        result["Score"].ShouldBe(3.14);
    }

    [Fact]
    public void ConvertJson_Bool_ReturnsBool()
    {
        var extract = new FakeChatClient().EnqueueText("{\"Count\": 0, \"BigNum\": 0, \"Score\": 0, \"Valid\": true, \"Weight\": 0}");
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "raw"));
        var result = new TwoStepAdapter(extract).Parse(MetaMulti, response);
        result["Valid"].ShouldBe(true);
    }

    [Fact]
    public void ConvertJson_Fallback_ReturnsRawText()
    {
        // Weight is float — not a standard type (string/int/long/double/bool), so it goes to GetRawText().
        var extract = new FakeChatClient().EnqueueText("{\"Count\": 0, \"BigNum\": 0, \"Score\": 0, \"Valid\": false, \"Weight\": 1.5}");
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "raw"));
        var result = new TwoStepAdapter(extract).Parse(MetaMulti, response);
        // GetRawText() returns the JSON text representation: "1.5"
        result["Weight"].ShouldBe("1.5");
    }

    [Fact]
    public void Format_EmbedsDemos()
    {
        var extract = new FakeChatClient();
        var adapter = new TwoStepAdapter(extract);
        var demo = new Example(
            new Dictionary<string, object?> { ["Question"] = "q1" },
            new Dictionary<string, object?> { ["Answer"] = "a1" });
        var msgs = adapter.Format(Meta, new Dictionary<string, object?> { ["Question"] = "q2" }, [demo]);
        msgs.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Parse_MissingOutputField_ReturnsNull()
    {
        var extract = new FakeChatClient().EnqueueText("{\"Other\": \"value\"}");
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "raw"));
        var result = new TwoStepAdapter(extract).Parse(Meta, response);
        result["Answer"].ShouldBeNull();
    }

    [Fact]
    public void Parse_NullResponseText_HandledGracefully()
    {
        var extract = new FakeChatClient().EnqueueText("{\"Answer\": \"result\"}");
        var response = new ChatResponse(messages: new List<ChatMessage>());
        var result = new TwoStepAdapter(extract).Parse(Meta, response);
        result["Answer"].ShouldBe("result");
    }
}

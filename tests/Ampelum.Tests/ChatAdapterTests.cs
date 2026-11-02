using SignalGrid.Ampelum;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

public sealed class ChatAdapterTests
{
    [Signature("Answer concisely.")]
    sealed record Qa : ISignature
    {
        [Input]  public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    [Signature("")]
    sealed record SilentSig : ISignature
    {
        [Input]  public required string A { get; init; }
        [Output] public string B { get; init; } = "";
    }

    [Signature("")]
    sealed record IntSig : ISignature
    {
        [Input]  public required string Q { get; init; }
        [Output] public int N { get; init; }
    }

    [Signature("")]
    sealed record EnumSig : ISignature
    {
        [Input]  public required string Q { get; init; }
        [Output] public DayOfWeek D { get; init; }
    }

    [Signature("")]
    sealed record GuidSig : ISignature
    {
        [Input]  public required string Q { get; init; }
        [Output] public Guid G { get; init; }
    }

    [Signature("")]
    sealed record DateTimeSig : ISignature
    {
        [Input]  public required string Q { get; init; }
        [Output] public DateTime When { get; init; }
    }

    [Signature("")]
    sealed record DateTimeOffsetSig : ISignature
    {
        [Input]  public required string Q { get; init; }
        [Output] public DateTimeOffset When { get; init; }
    }

    [Signature("")]
    sealed record TwoFieldSig : ISignature
    {
        [Input]  public required string Q { get; init; }
        [Output] public string First { get; init; } = "";
        [Output] public string Second { get; init; } = "";
    }

    static readonly SignatureMetadata Meta = SignatureMetadataCache.For<Qa>();
    static readonly ChatAdapter Sut = new();

    // -------- Format --------
    [Fact]
    public void Format_IncludesSystemInstructionsAndUserMarkers()
    {
        var msgs = Sut.Format(Meta, new Dictionary<string, object?> { ["Question"] = "Capital of France?" }, []);
        msgs.Count.ShouldBe(2);
        msgs[0].Role.ShouldBe(ChatRole.System);
        msgs[0].Text.ShouldContain("Answer concisely.");
        msgs[0].Text.ShouldContain("[[ ## Question ## ]]");
        msgs[0].Text.ShouldContain("[[ ## Answer ## ]]");
        msgs[1].Role.ShouldBe(ChatRole.User);
        msgs[1].Text.ShouldContain("[[ ## Question ## ]]");
        msgs[1].Text.ShouldContain("Capital of France?");
    }

    [Fact]
    public void Format_EmbedsDemos()
    {
        var demo = new Example(
            new Dictionary<string, object?> { ["Question"] = "2+2?" },
            new Dictionary<string, object?> { ["Answer"] = "4" });
        var msgs = Sut.Format(Meta, new Dictionary<string, object?> { ["Question"] = "x" }, [demo]);
        msgs.Count.ShouldBe(4);  // system, demo-user, demo-assistant, user
        msgs[1].Role.ShouldBe(ChatRole.User);
        msgs[1].Text.ShouldContain("2+2?");
        msgs[2].Role.ShouldBe(ChatRole.Assistant);
        msgs[2].Text.ShouldContain("4");
    }

    [Fact]
    public void Format_MissingInput_Throws() =>
        Should.Throw<SignalGrid.Ampelum.Exceptions.AdapterFormatException>(() =>
            Sut.Format(Meta, new Dictionary<string, object?>(), []));

    [Fact]
    public void Format_DemoMissingInput_Throws()
    {
        var demo = new Example(
            new Dictionary<string, object?>(),  // missing Question
            new Dictionary<string, object?> { ["Answer"] = "4" });
        Should.Throw<SignalGrid.Ampelum.Exceptions.AdapterFormatException>(() =>
            Sut.Format(Meta, new Dictionary<string, object?> { ["Question"] = "x" }, [demo]));
    }

    [Fact]
    public void Format_DemoMissingOutput_Throws()
    {
        var demo = new Example(
            new Dictionary<string, object?> { ["Question"] = "2+2?" },
            new Dictionary<string, object?>());  // missing Answer
        Should.Throw<SignalGrid.Ampelum.Exceptions.AdapterFormatException>(() =>
            Sut.Format(Meta, new Dictionary<string, object?> { ["Question"] = "x" }, [demo]));
    }

    [Fact]
    public void Format_NoInstructions_StillProducesSystemPrompt()
    {
        var meta = SignatureMetadataCache.For<SilentSig>();
        var msgs = Sut.Format(meta, new Dictionary<string, object?> { ["A"] = "x" }, []);
        msgs[0].Role.ShouldBe(ChatRole.System);
        msgs[0].Text.ShouldContain("[[ ## A ## ]]");
    }

    // -------- Parse --------
    [Fact]
    public void Parse_ExtractsMarkedSection()
    {
        var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant,
            "[[ ## Answer ## ]]\nParis"));
        var outs = Sut.Parse(Meta, resp);
        outs["Answer"].ShouldBe("Paris");
    }

    [Fact]
    public void Parse_MissingSection_Throws() =>
        Should.Throw<SignalGrid.Ampelum.Exceptions.AdapterParseException>(() =>
            Sut.Parse(Meta, new ChatResponse(new ChatMessage(ChatRole.Assistant, "just Paris"))));

    [Fact]
    public void Parse_OutOfOrderAndExtraSections_Succeeds()
    {
        var text = "[[ ## Junk ## ]]\nignored\n[[ ## Answer ## ]]\nParis";
        var outs = Sut.Parse(Meta, new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        outs["Answer"].ShouldBe("Paris");
    }

    [Fact]
    public void Parse_EmptyResponseMessages_Throws() =>
        Should.Throw<SignalGrid.Ampelum.Exceptions.AdapterParseException>(() =>
            Sut.Parse(Meta, new ChatResponse(messages: new List<ChatMessage>())));

    // -------- FormatResponse --------
    [Fact]
    public void FormatResponse_RoundTripsThroughParse()
    {
        var outs = new Dictionary<string, object?> { ["Answer"] = "Paris" };
        var resp = Sut.FormatResponse(Meta, outs);
        var parsed = Sut.Parse(Meta, resp);
        parsed["Answer"].ShouldBe("Paris");
    }

    [Fact]
    public void FormatResponse_MissingOutput_Throws() =>
        Should.Throw<SignalGrid.Ampelum.Exceptions.AdapterFormatException>(() =>
            Sut.FormatResponse(Meta, new Dictionary<string, object?>()));

    [Fact]
    public void Adapter_ImplementsITestableAdapter() =>
        Sut.ShouldBeAssignableTo<ITestableAdapter>();

    // -------- Type conversion in Parse --------
    [Fact]
    public void Parse_HandlesIntField()
    {
        var m = SignatureMetadataCache.For<IntSig>();
        var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## N ## ]]\n42"));
        Sut.Parse(m, resp)["N"].ShouldBe(42);
    }

    [Fact]
    public void Parse_HandlesEnumField()
    {
        var m = SignatureMetadataCache.For<EnumSig>();
        var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## D ## ]]\nMonday"));
        Sut.Parse(m, resp)["D"].ShouldBe(DayOfWeek.Monday);
    }

    [Fact]
    public void Parse_HandlesGuidField()
    {
        var m = SignatureMetadataCache.For<GuidSig>();
        var g = Guid.NewGuid();
        var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant, $"[[ ## G ## ]]\n{g}"));
        Sut.Parse(m, resp)["G"].ShouldBe(g);
    }

    [Fact]
    public void Parse_HandlesDateTimeField()
    {
        var m = SignatureMetadataCache.For<DateTimeSig>();
        var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## When ## ]]\n2026-04-22T12:00:00Z"));
        var v = (DateTime)Sut.Parse(m, resp)["When"]!;
        v.Year.ShouldBe(2026);
    }

    [Fact]
    public void Parse_HandlesDateTimeOffsetField()
    {
        var m = SignatureMetadataCache.For<DateTimeOffsetSig>();
        var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## When ## ]]\n2026-04-22T12:00:00+02:00"));
        var v = (DateTimeOffset)Sut.Parse(m, resp)["When"]!;
        v.Year.ShouldBe(2026);
    }

    [Fact]
    public void Parse_NonHashMarkerBrackets_NotTreatedAsSection()
    {
        // Text contains [[not-a-marker]] â€” brackets but no ## inner markers.
        // TryMatchMarker should return false for these, so they are treated as body text,
        // and the real [[ ## Answer ## ]] section is still found correctly.
        var text = "[[not-a-marker]]\nsome body\n[[ ## Answer ## ]]\nParis";
        var outs = Sut.Parse(Meta, new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        outs["Answer"].ShouldBe("Paris");
    }

    // -------- Same-line marker value --------
    [Fact]
    public void Parse_SameLineMarkerValue_Succeeds()
    {
        // Some providers (e.g. Gemini 3.8 Flash, GPT-OSS 20B via OpenRouter) emit the value on
        // the same line as its marker instead of the next line. The parser must tolerate this.
        var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## Answer ## ]] Paris"));
        var outs = Sut.Parse(Meta, resp);
        outs["Answer"].ShouldBe("Paris");
    }

    [Fact]
    public void Parse_SameLineMarkerValue_EnumField_Succeeds()
    {
        // Mirrors the reported failure: "[[ ## Sentiment ## ]] Positive" on one line.
        var m = SignatureMetadataCache.For<EnumSig>();
        var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## D ## ]] Monday"));
        Sut.Parse(m, resp)["D"].ShouldBe(DayOfWeek.Monday);
    }

    [Fact]
    public void Parse_NextLineMarkerValue_StillSucceeds_NoRegression()
    {
        // The pre-existing next-line format must keep working unchanged.
        var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## Answer ## ]]\nParis"));
        var outs = Sut.Parse(Meta, resp);
        outs["Answer"].ShouldBe("Paris");
    }

    [Fact]
    public void Parse_SameLineValueThenAnotherSection_DoesNotSwallowNextMarker()
    {
        // Same-line value on one field must not swallow a subsequent field's marker/value.
        var m = SignatureMetadataCache.For<TwoFieldSig>();
        var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant,
            "[[ ## First ## ]] one\n[[ ## Second ## ]] two"));
        var outs = Sut.Parse(m, resp);
        outs["First"].ShouldBe("one");
        outs["Second"].ShouldBe("two");
    }

    [Fact]
    public void Parse_MissingMarkerEntirely_StillThrowsWithClearError()
    {
        // Genuinely malformed response (no marker at all) must still fail loudly, not be
        // silently accepted by the new same-line tolerance.
        var ex = Should.Throw<SignalGrid.Ampelum.Exceptions.AdapterParseException>(() =>
            Sut.Parse(Meta, new ChatResponse(new ChatMessage(ChatRole.Assistant, "Positive"))));
        ex.Message.ShouldContain("missing section '[[ ## Answer ## ]]'");
    }
}

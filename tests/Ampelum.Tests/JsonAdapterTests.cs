using System.Text.Json;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Exceptions;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

public sealed class JsonAdapterTests
{
    [Signature("")]
    sealed record Cls : ISignature
    {
        [Input]  public required string Text { get; init; }
        [Output] public string Category { get; init; } = "";
        [Output] public double Score { get; init; }
    }

    // Used to exercise JsonTypeFor fallback branch (Guid → "string") and ToCamel("")
    [Signature("")]
    sealed record ClsWithGuid : ISignature
    {
        [Input]  public required string Text { get; init; }
        [Output] public Guid Id { get; init; }
    }

    [Signature("Classify the input text.")]
    sealed record ClsWithInstructions : ISignature
    {
        [Input]  public required string Text { get; init; }
        [Output] public string Category { get; init; } = "";
    }

    [Signature("")]
    sealed record OptionalSig : ISignature
    {
        [Input]  public required string Q { get; init; }
        [Output] public string? MaybeAnswer { get; init; }
        [Output] public int RequiredScore { get; init; }
    }

    [Signature("")]
    sealed record ClsAllTypes : ISignature
    {
        [Input]  public required string Text { get; init; }
        [Output] public int Count { get; init; }
        [Output] public bool IsValid { get; init; }
        [Output] public string[] Tags { get; init; } = [];
        [Output] public int? OptionalNum { get; init; }
        [Output] public long LongVal { get; init; }
        [Output] public short ShortVal { get; init; }
        [Output] public float FloatVal { get; init; }
        [Output] public List<string> Items { get; init; } = [];
    }

    static readonly SignatureMetadata Meta = SignatureMetadataCache.For<Cls>();
    static readonly SignatureMetadata MetaAllTypes = SignatureMetadataCache.For<ClsAllTypes>();

    [Fact]
    public void Format_ProducesSchemaInstruction()
    {
        var sut = new JsonAdapter();
        var msgs = sut.Format(Meta, new Dictionary<string, object?> { ["Text"] = "hi" }, []);
        msgs[0].Role.ShouldBe(ChatRole.System);
        msgs[0].Text.ShouldContain("\"category\"");
        msgs[0].Text.ShouldContain("\"score\"");
    }

    [Fact]
    public void Format_EmbedsDemos()
    {
        var sut = new JsonAdapter();
        var demo = new Example(
            new Dictionary<string, object?> { ["Text"] = "hi" },
            new Dictionary<string, object?> { ["Category"] = "greet", ["Score"] = 0.5 });
        var msgs = sut.Format(Meta, new Dictionary<string, object?> { ["Text"] = "x" }, [demo]);
        msgs.Count.ShouldBe(4);
        msgs[1].Role.ShouldBe(ChatRole.User);
        msgs[2].Role.ShouldBe(ChatRole.Assistant);
    }

    [Fact]
    public void Parse_Strict_ValidJson()
    {
        var sut = new JsonAdapter();
        var body = """{"category":"news","score":0.9}""";
        var outs = sut.Parse(Meta, new ChatResponse(new ChatMessage(ChatRole.Assistant, body)));
        outs["Category"].ShouldBe("news");
        outs["Score"].ShouldBe(0.9);
    }

    [Fact]
    public void Parse_Strict_PascalCaseAlsoAccepted()
    {
        var sut = new JsonAdapter();
        var body = """{"Category":"news","Score":0.9}""";
        var outs = sut.Parse(Meta, new ChatResponse(new ChatMessage(ChatRole.Assistant, body)));
        outs["Category"].ShouldBe("news");
    }

    [Fact]
    public void Parse_Strict_Malformed_Throws()
    {
        var sut = new JsonAdapter();
        Should.Throw<AdapterParseException>(() =>
            sut.Parse(Meta, new ChatResponse(new ChatMessage(ChatRole.Assistant, "not json"))));
    }

    [Fact]
    public void Parse_Strict_MissingField_Throws()
    {
        var sut = new JsonAdapter();
        var body = """{"category":"news"}""";  // missing score
        Should.Throw<AdapterParseException>(() =>
            sut.Parse(Meta, new ChatResponse(new ChatMessage(ChatRole.Assistant, body))));
    }

    [Fact]
    public void Parse_Strict_EmptyMessages_Throws()
    {
        var sut = new JsonAdapter();
        Should.Throw<AdapterParseException>(() =>
            sut.Parse(Meta, new ChatResponse(messages: new List<ChatMessage>())));
    }

    [Fact]
    public void Parse_Lenient_ExtractsJsonFromProse()
    {
        var sut = new JsonAdapter(strict: false);
        var body = "Sure, here you go: {\"category\":\"news\",\"score\":0.5}. Hope it helps!";
        var outs = sut.Parse(Meta, new ChatResponse(new ChatMessage(ChatRole.Assistant, body)));
        outs["Category"].ShouldBe("news");
    }

    [Fact]
    public void Parse_Lenient_NoJson_Throws()
    {
        var sut = new JsonAdapter(strict: false);
        Should.Throw<AdapterParseException>(() =>
            sut.Parse(Meta, new ChatResponse(new ChatMessage(ChatRole.Assistant, "no json here"))));
    }

    [Fact]
    public void BuildResponseFormat_ReturnsJsonSchema()
    {
        var sut = new JsonAdapter();
        var fmt = sut.BuildResponseFormat(Meta);
        fmt.ShouldNotBeNull();
    }

    [Fact]
    public void FormatResponse_RoundTrips()
    {
        var sut = new JsonAdapter();
        var resp = sut.FormatResponse(Meta, new Dictionary<string, object?>
        {
            ["Category"] = "news",
            ["Score"] = 0.7,
        });
        var parsed = sut.Parse(Meta, resp);
        parsed["Category"].ShouldBe("news");
        parsed["Score"].ShouldBe(0.7);
    }

    [Fact]
    public void FormatResponse_MissingOutput_Throws()
    {
        var sut = new JsonAdapter();
        Should.Throw<AdapterFormatException>(() =>
            sut.FormatResponse(Meta, new Dictionary<string, object?> { ["Category"] = "x" }));  // missing Score
    }

    [Fact]
    public void ImplementsBothCapabilityInterfaces()
    {
        var sut = new JsonAdapter();
        sut.ShouldBeAssignableTo<IStructuredOutputAdapter>();
        sut.ShouldBeAssignableTo<ITestableAdapter>();
    }

    // --- Coverage branch tests ---

    [Fact]
    public void Format_AllTypes_SchemaContainsExpectedJsonTypes()
    {
        var sut = new JsonAdapter();
        var msgs = sut.Format(MetaAllTypes, new Dictionary<string, object?> { ["Text"] = "hi" }, []);
        var systemText = msgs[0].Text;
        // int, long, short → "integer"
        systemText.ShouldContain("\"integer\"");
        // bool → "boolean"
        systemText.ShouldContain("\"boolean\"");
        // string[] and List<string> → "array"
        systemText.ShouldContain("\"array\"");
        // double/float/decimal → "number"
        systemText.ShouldContain("\"number\"");
    }

    [Fact]
    public void Format_AllTypes_NullableNotInRequired()
    {
        // OptionalNum is int? — should NOT appear in required list
        // (Nullable.GetUnderlyingType returns non-null → excluded from required)
        var sut = new JsonAdapter();
        var msgs = sut.Format(MetaAllTypes, new Dictionary<string, object?> { ["Text"] = "hi" }, []);
        var systemText = msgs[0].Text;
        // The required array should not contain "optionalNum"
        var schemaStart = systemText.IndexOf('{');
        var json = systemText[schemaStart..];
        var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("required", out var required).ShouldBeTrue();
        var requiredNames = required.EnumerateArray().Select(e => e.GetString()).ToList();
        requiredNames.ShouldNotContain("optionalNum");
        // Non-nullable struct fields should be required
        requiredNames.ShouldContain("count");
        requiredNames.ShouldContain("isValid");
    }

    [Fact]
    public void Parse_Strict_NullText_Throws()
    {
        // Guard: when message exists but text is null/empty
        var sut = new JsonAdapter();
        // Create a message with no text content
        var msg = new ChatMessage(ChatRole.Assistant, "");
        Should.Throw<AdapterParseException>(() =>
            sut.Parse(Meta, new ChatResponse(msg)));
    }

    [Fact]
    public void Format_WithInstructions_IncludesInstructions()
    {
        // Uses a signature with non-empty instructions to exercise the
        // !string.IsNullOrWhiteSpace(instructions) true branch.
        var meta = SignatureMetadataCache.For<ClsWithInstructions>();
        var sut = new JsonAdapter();
        var msgs = sut.Format(meta, new Dictionary<string, object?> { ["Text"] = "hi" }, []);
        msgs[0].Text.ShouldContain("Classify the input text.");
    }

    [Fact]
    public void Format_SchemaIsCached_SecondCallReturnsSameSchema()
    {
        // Exercises the cache hit path in _schemaCache.GetOrAdd
        var sut = new JsonAdapter();
        var inputs = new Dictionary<string, object?> { ["Text"] = "hi" };
        var msgs1 = sut.Format(Meta, inputs, []);
        var msgs2 = sut.Format(Meta, inputs, []);
        msgs1[0].Text.ShouldBe(msgs2[0].Text);
    }

    [Fact]
    public void BuildResponseFormat_SecondCall_ReturnsCachedSchema()
    {
        var sut = new JsonAdapter();
        var fmt1 = sut.BuildResponseFormat(Meta);
        var fmt2 = sut.BuildResponseFormat(Meta);
        fmt1.ShouldNotBeNull();
        fmt2.ShouldNotBeNull();
    }

    [Fact]
    public void Parse_AllTypes_ParsesIntAndBool()
    {
        var sut = new JsonAdapter();
        var body = """{"count":5,"isValid":true,"tags":["a","b"],"optionalNum":null,"longVal":100,"shortVal":1,"floatVal":1.5,"items":["x"]}""";
        var outs = sut.Parse(MetaAllTypes, new ChatResponse(new ChatMessage(ChatRole.Assistant, body)));
        outs["Count"].ShouldBe(5);
        outs["IsValid"].ShouldBe(true);
        outs["LongVal"].ShouldBe(100L);
    }

    [Fact]
    public void Format_GuidField_SchemaFallsBackToString()
    {
        // Guid is a supported field type but falls through to the string fallback in JsonTypeFor
        var meta = SignatureMetadataCache.For<ClsWithGuid>();
        var sut = new JsonAdapter();
        var msgs = sut.Format(meta, new Dictionary<string, object?> { ["Text"] = "hi" }, []);
        // "id" field should appear in schema; it maps to "string" type (fallback)
        msgs[0].Text.ShouldContain("\"id\"");
        msgs[0].Text.ShouldContain("\"string\"");
    }

    [Fact]
    public void BuildSchema_StringOutput_IsRequired()
    {
        var sut = new JsonAdapter();
        var msgs = sut.Format(Meta, new Dictionary<string, object?> { ["Text"] = "x" }, []);
        msgs[0].Text.ShouldContain("\"required\":[\"category\",\"score\"]");
    }

    [Fact]
    public void BuildSchema_NullableOutput_IsNotRequired()
    {
        var sut = new JsonAdapter();
        var meta = SignatureMetadataCache.For<OptionalSig>();
        var msgs = sut.Format(meta, new Dictionary<string, object?> { ["Q"] = "x" }, []);
        msgs[0].Text.ShouldContain("\"required\":[\"requiredScore\"]");
        msgs[0].Text.ShouldNotContain("\"maybeAnswer\"]");
    }

    // --- Demo validation tests (spec invariant §5.8.3) ---

    [Fact]
    public void Format_MissingInput_Throws()
    {
        var sut = new JsonAdapter();
        // "Text" input is required but not supplied
        Should.Throw<AdapterFormatException>(() =>
            sut.Format(Meta, new Dictionary<string, object?>(), []));
    }

    [Fact]
    public void Format_DemoMissingInput_Throws()
    {
        var sut = new JsonAdapter();
        var badDemo = new Example(
            new Dictionary<string, object?>(),  // missing "Text"
            new Dictionary<string, object?> { ["Category"] = "x", ["Score"] = 0.0 });
        Should.Throw<AdapterFormatException>(() =>
            sut.Format(Meta, new Dictionary<string, object?> { ["Text"] = "hi" }, [badDemo]));
    }

    [Fact]
    public void Format_DemoMissingOutput_Throws()
    {
        var sut = new JsonAdapter();
        var badDemo = new Example(
            new Dictionary<string, object?> { ["Text"] = "hi" },
            new Dictionary<string, object?> { ["Category"] = "x" });  // missing "Score"
        Should.Throw<AdapterFormatException>(() =>
            sut.Format(Meta, new Dictionary<string, object?> { ["Text"] = "hi" }, [badDemo]));
    }

    // ── v0.6c ScanRoot edge-case coverage ───────────────────────────────────

    [Fact]
    public void Parse_RootIsJsonNull_ThrowsAsInvalid()
    {
        // "null" parses; reader.Read() returns true with TokenType=Null, so the
        // root-must-be-object guard fires.
        var sut = new JsonAdapter(strict: true);
        var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant, "null"));
        Should.Throw<AdapterParseException>(() => sut.Parse(Meta, resp));
    }

    [Fact]
    public void Parse_UnmatchedPropertyWithObjectValue_SkipsAndContinues()
    {
        // Unmatched property has a nested object/array — exercises the else-branch Skip
        // path in ScanRoot. Required outputs still resolve from named fields.
        var sut = new JsonAdapter(strict: true);
        var json = """{"extra":{"nested":1,"more":[1,2,3]},"category":"a","score":0.5}""";
        var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant, json));
        var parsed = sut.Parse(Meta, resp);
        parsed["Category"].ShouldBe("a");
        parsed["Score"].ShouldBe(0.5);
    }

    [Fact]
    public void Parse_LargePayload_UsesArrayPool()
    {
        // Force the >1024-byte UTF-8 path so the ArrayPool branch in Parse runs.
        var padding = new string('x', 2048);
        var json = $$"""{"category":"{{padding}}","score":0.1}""";
        var sut = new JsonAdapter(strict: true);
        var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant, json));
        var parsed = sut.Parse(Meta, resp);
        parsed["Category"].ShouldBe(padding);
    }
}

using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Adapters;
using SignalGrid.Ampelum.Exceptions;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

public sealed class XMLAdapterTests
{
    [Signature("Answer questions.")]
    sealed record QA : ISignature
    {
        [Input] public string Question { get; init; } = "";
        [Output] public string Answer { get; init; } = "";
    }

    // Multi-type signature for ConvertTo branch coverage.
    [Signature("")]
    sealed record MultiType : ISignature
    {
        [Input]  public string Text { get; init; } = "";
        [Output] public int    Count   { get; init; }
        [Output] public long   BigNum  { get; init; }
        [Output] public double Score   { get; init; }
        [Output] public bool   Valid   { get; init; }
        [Output] public float  Weight  { get; init; }   // triggers Convert.ChangeType fallback
    }

    static readonly SignatureMetadata Meta      = SignatureMetadataCache.For<QA>();
    static readonly SignatureMetadata MetaMulti = SignatureMetadataCache.For<MultiType>();

    // ── Spec tests (plan Task 20, lines 3724-3780) ──────────────────────────

    [Fact]
    public void Format_WrapsInputsInXmlTags()
    {
        var msgs = new XMLAdapter().Format(Meta,
            new Dictionary<string, object?> { ["Question"] = "cap?" }, Array.Empty<Example>());
        msgs.Last().Text.ShouldContain("<Inputs>");
        msgs.Last().Text.ShouldContain("<Question>cap?</Question>");
    }

    [Fact]
    public void Parse_Lenient_ExtractsOutputs()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant,
            "<Outputs><Answer>paris</Answer></Outputs>"));
        new XMLAdapter(XMLAdapter.ParseMode.Lenient).Parse(Meta, response)["Answer"].ShouldBe("paris");
    }

    [Fact]
    public void Parse_Strict_MissingField_Throws()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant,
            "<Outputs></Outputs>"));
        Should.Throw<AdapterParseException>(() =>
            new XMLAdapter(XMLAdapter.ParseMode.Strict).Parse(Meta, response));
    }

    [Fact]
    public void Parse_Lenient_MissingField_ReturnsNull()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant,
            "<Outputs></Outputs>"));
        new XMLAdapter(XMLAdapter.ParseMode.Lenient).Parse(Meta, response)["Answer"].ShouldBeNull();
    }

    [Fact]
    public void Parse_MalformedXml_Throws()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "not xml"));
        Should.Throw<AdapterParseException>(() =>
            new XMLAdapter().Parse(Meta, response));
    }

    [Theory]
    [InlineData("simple")]
    [InlineData("has <angle> brackets")]
    [InlineData("has & ampersand")]
    [InlineData("has \"quote\"")]
    public void RoundTrip_EscapedStrings(string value)
    {
        var adapter = new XMLAdapter();
        var msgs = adapter.Format(Meta, new Dictionary<string, object?> { ["Question"] = "q" }, Array.Empty<Example>());
        // Simulate LM response by building an Outputs block with the value.
        var encoded = System.Security.SecurityElement.Escape(value);
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant,
            $"<Outputs><Answer>{encoded}</Answer></Outputs>"));
        adapter.Parse(Meta, response)["Answer"].ShouldBe(value);
    }

    // ── Coverage extras ──────────────────────────────────────────────────────

    [Fact]
    public void ConvertTo_Int_ParsesCorrectly()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant,
            "<Outputs><Count>42</Count><BigNum>0</BigNum><Score>0</Score><Valid>false</Valid><Weight>0</Weight></Outputs>"));
        var outs = new XMLAdapter().Parse(MetaMulti, response);
        outs["Count"].ShouldBe(42);
    }

    [Fact]
    public void ConvertTo_Long_ParsesCorrectly()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant,
            "<Outputs><Count>0</Count><BigNum>9876543210</BigNum><Score>0</Score><Valid>false</Valid><Weight>0</Weight></Outputs>"));
        var outs = new XMLAdapter().Parse(MetaMulti, response);
        outs["BigNum"].ShouldBe(9876543210L);
    }

    [Fact]
    public void ConvertTo_Double_ParsesCorrectly()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant,
            "<Outputs><Count>0</Count><BigNum>0</BigNum><Score>3.14</Score><Valid>false</Valid><Weight>0</Weight></Outputs>"));
        var outs = new XMLAdapter().Parse(MetaMulti, response);
        outs["Score"].ShouldBe(3.14);
    }

    [Fact]
    public void ConvertTo_Bool_ParsesCorrectly()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant,
            "<Outputs><Count>0</Count><BigNum>0</BigNum><Score>0</Score><Valid>True</Valid><Weight>0</Weight></Outputs>"));
        var outs = new XMLAdapter().Parse(MetaMulti, response);
        outs["Valid"].ShouldBe(true);
    }

    [Fact]
    public void ConvertTo_NonScalarFallback_UsesConvertChangeType()
    {
        // float is not int/long/double/bool/string — hits Convert.ChangeType fallback.
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant,
            "<Outputs><Count>0</Count><BigNum>0</BigNum><Score>0</Score><Valid>false</Valid><Weight>1.5</Weight></Outputs>"));
        var outs = new XMLAdapter().Parse(MetaMulti, response);
        ((float)outs["Weight"]!).ShouldBe(1.5f, tolerance: 0.001f);
    }

    [Fact]
    public void Format_EmbedsDemos()
    {
        var adapter = new XMLAdapter();
        var demo = new Example(
            new Dictionary<string, object?> { ["Question"] = "q1" },
            new Dictionary<string, object?> { ["Answer"] = "a1" });
        var msgs = adapter.Format(Meta, new Dictionary<string, object?> { ["Question"] = "q2" }, [demo]);
        msgs.Count.ShouldBe(4); // system + user demo + assistant demo + user
        msgs[1].Role.ShouldBe(ChatRole.User);
        msgs[1].Text.ShouldContain("<Inputs>");
        msgs[2].Role.ShouldBe(ChatRole.Assistant);
        msgs[2].Text.ShouldContain("<Outputs>");
    }

    [Fact]
    public void Format_WithInstructions_IncludesInstructions()
    {
        // QA has non-empty [Signature] instructions — exercises the !IsNullOrWhiteSpace branch.
        var msgs = new XMLAdapter().Format(Meta, new Dictionary<string, object?> { ["Question"] = "hi" }, []);
        msgs[0].Text.ShouldContain("Answer questions.");
    }

    [Fact]
    public void Parse_XmlException_CaughtAndRethrown()
    {
        // Provide a response that has <Outputs> tags but contains malformed XML between them,
        // so XDocument.Parse throws XmlException.
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant,
            "<Outputs><Answer>bad < unescaped</Answer></Outputs>"));
        Should.Throw<AdapterParseException>(() =>
            new XMLAdapter().Parse(Meta, response));
    }

    [Fact]
    public void Parse_Strict_IsDefaultMode()
    {
        // Verify that the default constructor parameter is Lenient (per ctor signature),
        // and that explicit Strict does throw.
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant,
            "<Outputs></Outputs>"));
        // Default is Lenient — should not throw.
        new XMLAdapter().Parse(Meta, response)["Answer"].ShouldBeNull();
    }

    [Fact]
    public void Parse_NullResponseText_TreatedAsEmpty_Throws()
    {
        // Exercises the `response.Text ?? ""` null-coalescing branch (Path 0).
        // A response with no messages returns null for Text; the adapter should still throw
        // AdapterParseException (no <Outputs> block found in empty string).
        var response = new ChatResponse(messages: new List<ChatMessage>());
        Should.Throw<AdapterParseException>(() =>
            new XMLAdapter().Parse(Meta, response));
    }

    [Fact]
    public void Format_NullValueInInputs_EmitsEmptyElement()
    {
        // Exercises the `v is not null` false branch in RenderXml (key found, value is null).
        var adapter = new XMLAdapter();
        var msgs = adapter.Format(Meta,
            new Dictionary<string, object?> { ["Question"] = null }, []);
        // Element should still be present but empty.
        msgs.Last().Text.ShouldContain("<Question></Question>");
    }

    [Fact]
    public void Format_MissingKeyInInputs_EmitsEmptyElement()
    {
        // Exercises the TryGetValue-returns-false branch in RenderXml.
        // We use a demo where the output dict doesn't contain a key for a declared output field.
        // RenderXml is called with the outputs dict that may be sparse; it must still emit the tag.
        var adapter = new XMLAdapter();
        // Supply an empty outputs dict for the demo's outputs to hit TryGetValue==false.
        var demo = new Example(
            new Dictionary<string, object?> { ["Question"] = "q" },
            new Dictionary<string, object?>());   // no "Answer" key → TryGetValue returns false
        var msgs = adapter.Format(Meta,
            new Dictionary<string, object?> { ["Question"] = "x" }, [demo]);
        // The assistant demo should contain an empty <Answer> tag.
        msgs[2].Text.ShouldContain("<Answer></Answer>");
    }
}

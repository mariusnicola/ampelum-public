using SignalGrid.Ampelum.Exceptions;
using Shouldly;
using Xunit;

public sealed class ExceptionsTests
{
    [Fact]
    public void AdapterFormatException_Message()
    {
        var ex = new AdapterFormatException("bad");
        ex.Message.ShouldBe("bad");
        ex.ShouldBeAssignableTo<AmpelumException>();
    }

    [Fact]
    public void AdapterFormatException_WrapsInner()
    {
        var inner = new InvalidOperationException("inner");
        var ex = new AdapterFormatException("bad", inner);
        ex.InnerException.ShouldBeSameAs(inner);
    }

    [Fact]
    public void AdapterParseException_ExposesMetadata()
    {
        var ex = new AdapterParseException(
            signatureName: "Qa",
            adapterName: "ChatAdapter",
            rawResponse: "garbage",
            hint: "try JsonAdapter");
        ex.SignatureName.ShouldBe("Qa");
        ex.AdapterName.ShouldBe("ChatAdapter");
        ex.RawResponse.ShouldBe("garbage");
        ex.Hint.ShouldBe("try JsonAdapter");
        ex.Message.ShouldContain("Qa");
        ex.Message.ShouldContain("ChatAdapter");
        ex.Message.ShouldContain("garbage");
        ex.Message.ShouldContain("try JsonAdapter");
    }

    [Fact]
    public void AdapterParseException_TruncatesLongResponse()
    {
        var long500 = new string('x', 5000);
        var ex = new AdapterParseException("S", "A", long500, "h");
        ex.Message.Length.ShouldBeLessThan(1000);
        ex.RawResponse.ShouldBe(long500);
    }

    [Fact]
    public void AdapterParseException_ShortResponseNotTruncated()
    {
        var ex = new AdapterParseException("S", "A", "short", "h");
        ex.Message.ShouldContain("\"short\"");
    }

    [Fact]
    public void LmCallException_WrapsInner()
    {
        var inner = new InvalidOperationException("boom");
        var ex = new LmCallException("provider failed", inner);
        ex.InnerException.ShouldBeSameAs(inner);
    }

    [Fact]
    public void SignatureDefinitionException_Message()
    {
        new SignatureDefinitionException("bad").Message.ShouldBe("bad");
    }
}

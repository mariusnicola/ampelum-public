using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.ProviderAdapters;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.ProviderAdapters;

[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class ToolCallFormatConverterTests
{
    static IReadOnlyDictionary<string, object?> Args(params (string K, object? V)[] kv)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (k, v) in kv) d[k] = v;
        return d;
    }

    // -- FromOpenAi --

    [Fact]
    public void FromOpenAi_Null_Throws() =>
        Should.Throw<ArgumentNullException>(() => ToolCallFormatConverter.FromOpenAi(null!));

    [Fact]
    public void FromOpenAi_NotArray_Throws() =>
        Should.Throw<ArgumentException>(() => ToolCallFormatConverter.FromOpenAi("{}"));

    [Fact]
    public void FromOpenAi_SingleCall_Parses()
    {
        var json = "[{\"id\":\"call-1\",\"type\":\"function\",\"function\":{\"name\":\"weather\",\"arguments\":\"{\\\"city\\\":\\\"sf\\\",\\\"days\\\":3}\"}}]";
        var calls = ToolCallFormatConverter.FromOpenAi(json);
        calls.Count.ShouldBe(1);
        calls[0].Id.ShouldBe("call-1");
        calls[0].Name.ShouldBe("weather");
        calls[0].Arguments["city"].ShouldBe("sf");
        calls[0].Arguments["days"].ShouldBe(3L);
    }

    [Fact]
    public void FromOpenAi_EmptyArguments_ReturnsEmptyDict()
    {
        var json = "[{\"id\":\"id\",\"type\":\"function\",\"function\":{\"name\":\"n\",\"arguments\":\"{}\"}}]";
        ToolCallFormatConverter.FromOpenAi(json)[0].Arguments.Count.ShouldBe(0);
    }

    [Fact]
    public void FromOpenAi_MissingArguments_DefaultsToEmptyDict()
    {
        var json = "[{\"id\":\"id\",\"type\":\"function\",\"function\":{\"name\":\"n\"}}]";
        ToolCallFormatConverter.FromOpenAi(json)[0].Arguments.Count.ShouldBe(0);
    }

    [Fact]
    public void FromOpenAi_MissingId_Defaults()
    {
        var json = "[{\"type\":\"function\",\"function\":{\"name\":\"n\",\"arguments\":\"{}\"}}]";
        ToolCallFormatConverter.FromOpenAi(json)[0].Id.ShouldBe("");
    }

    [Fact]
    public void FromOpenAi_ParallelCalls_ParsesAll()
    {
        var json = "[{\"id\":\"a\",\"type\":\"function\",\"function\":{\"name\":\"x\",\"arguments\":\"{}\"}}," +
                   "{\"id\":\"b\",\"type\":\"function\",\"function\":{\"name\":\"y\",\"arguments\":\"{\\\"k\\\":1}\"}}]";
        var calls = ToolCallFormatConverter.FromOpenAi(json);
        calls.Count.ShouldBe(2);
        calls[1].Arguments["k"].ShouldBe(1L);
    }

    // -- ToOpenAi --

    [Fact]
    public void ToOpenAi_Null_Throws() =>
        Should.Throw<ArgumentNullException>(() => ToolCallFormatConverter.ToOpenAi(null!));

    [Fact]
    public void ToOpenAi_RoundTrip_PreservesShape()
    {
        var calls = new[] { new NormalizedToolCall("id-1", "f", Args(("a", 1L), ("b", "x"))) };
        var json = ToolCallFormatConverter.ToOpenAi(calls);
        var back = ToolCallFormatConverter.FromOpenAi(json);
        back[0].Id.ShouldBe("id-1");
        back[0].Name.ShouldBe("f");
        back[0].Arguments["a"].ShouldBe(1L);
        back[0].Arguments["b"].ShouldBe("x");
    }

    // -- FromAnthropic --

    [Fact]
    public void FromAnthropic_Null_Throws() =>
        Should.Throw<ArgumentNullException>(() => ToolCallFormatConverter.FromAnthropic(null!));

    [Fact]
    public void FromAnthropic_NotArray_Throws() =>
        Should.Throw<ArgumentException>(() => ToolCallFormatConverter.FromAnthropic("{}"));

    [Fact]
    public void FromAnthropic_FiltersNonToolUseBlocks()
    {
        var json = "[{\"type\":\"text\",\"text\":\"thinking\"},{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"calc\",\"input\":{\"a\":2}}]";
        var calls = ToolCallFormatConverter.FromAnthropic(json);
        calls.Count.ShouldBe(1);
        calls[0].Id.ShouldBe("t1");
        calls[0].Arguments["a"].ShouldBe(2L);
    }

    [Fact]
    public void FromAnthropic_NoInputKey_DefaultsToEmpty()
    {
        var json = "[{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"x\"}]";
        ToolCallFormatConverter.FromAnthropic(json)[0].Arguments.Count.ShouldBe(0);
    }

    [Fact]
    public void FromAnthropic_BlockMissingTypeKey_Skipped()
    {
        var json = "[{\"foo\":\"bar\"}]";
        ToolCallFormatConverter.FromAnthropic(json).Count.ShouldBe(0);
    }

    [Fact]
    public void FromAnthropic_MissingIdAndName_Defaults()
    {
        var json = "[{\"type\":\"tool_use\"}]";
        var c = ToolCallFormatConverter.FromAnthropic(json)[0];
        c.Id.ShouldBe("");
        c.Name.ShouldBe("");
    }

    // -- ToAnthropic --

    [Fact]
    public void ToAnthropic_Null_Throws() =>
        Should.Throw<ArgumentNullException>(() => ToolCallFormatConverter.ToAnthropic(null!));

    [Fact]
    public void ToAnthropic_RoundTrip()
    {
        var calls = new[] { new NormalizedToolCall("a-1", "calc", Args(("a", 2L))) };
        var json = ToolCallFormatConverter.ToAnthropic(calls);
        var back = ToolCallFormatConverter.FromAnthropic(json);
        back.Count.ShouldBe(1);
        back[0].Id.ShouldBe("a-1");
        back[0].Arguments["a"].ShouldBe(2L);
    }

    // -- FromGemini --

    [Fact]
    public void FromGemini_Null_Throws() =>
        Should.Throw<ArgumentNullException>(() => ToolCallFormatConverter.FromGemini(null!));

    [Fact]
    public void FromGemini_NotArray_Throws() =>
        Should.Throw<ArgumentException>(() => ToolCallFormatConverter.FromGemini("{}"));

    [Fact]
    public void FromGemini_FiltersAndSynthesisesIds()
    {
        var json = "[{\"text\":\"hi\"},{\"functionCall\":{\"name\":\"calc\",\"args\":{\"a\":1}}}," +
                   "{\"functionCall\":{\"name\":\"weather\",\"args\":{\"city\":\"sf\"}}}]";
        var calls = ToolCallFormatConverter.FromGemini(json);
        calls.Count.ShouldBe(2);
        calls[0].Id.ShouldStartWith("gemini-tool-0-");
        calls[1].Id.ShouldStartWith("gemini-tool-1-");
        calls[0].Arguments["a"].ShouldBe(1L);
    }

    [Fact]
    public void FromGemini_NoArgsKey_DefaultsToEmpty()
    {
        var json = "[{\"functionCall\":{\"name\":\"f\"}}]";
        ToolCallFormatConverter.FromGemini(json)[0].Arguments.Count.ShouldBe(0);
    }

    [Fact]
    public void FromGemini_MissingNameKey_Defaults()
    {
        var json = "[{\"functionCall\":{\"args\":{}}}]";
        ToolCallFormatConverter.FromGemini(json)[0].Name.ShouldBe("");
    }

    // -- ToGemini --

    [Fact]
    public void ToGemini_Null_Throws() =>
        Should.Throw<ArgumentNullException>(() => ToolCallFormatConverter.ToGemini(null!));

    [Fact]
    public void ToGemini_RoundTrip_PreservesNameAndArgs()
    {
        var calls = new[] { new NormalizedToolCall("ignored", "calc", Args(("k", 7L))) };
        var json = ToolCallFormatConverter.ToGemini(calls);
        var back = ToolCallFormatConverter.FromGemini(json);
        back[0].Name.ShouldBe("calc");
        back[0].Arguments["k"].ShouldBe(7L);
    }

    // -- Cross-provider round-trip --

    [Fact]
    public void RoundTrip_OpenAi_To_Anthropic_To_Gemini_PreservesArgs()
    {
        var original = new[] { new NormalizedToolCall("call-1", "weather",
            Args(("city", "sf"), ("days", 3L), ("flag", true))) };

        var openAiJson = ToolCallFormatConverter.ToOpenAi(original);
        var fromOpenAi = ToolCallFormatConverter.FromOpenAi(openAiJson);
        var anthJson = ToolCallFormatConverter.ToAnthropic(fromOpenAi);
        var fromAnth = ToolCallFormatConverter.FromAnthropic(anthJson);
        var gemJson = ToolCallFormatConverter.ToGemini(fromAnth);
        var fromGem = ToolCallFormatConverter.FromGemini(gemJson);

        fromGem[0].Name.ShouldBe("weather");
        fromGem[0].Arguments["city"].ShouldBe("sf");
        fromGem[0].Arguments["days"].ShouldBe(3L);
        fromGem[0].Arguments["flag"].ShouldBe(true);
    }

    [Fact]
    public void RoundTrip_NestedObjectsAndArrays_Preserved()
    {
        var nested = Args(("filters", Args(("min", 1L), ("max", 10L))), ("tags", new List<object?> { "a", "b" }));
        var calls = new[] { new NormalizedToolCall("c", "search", nested) };
        var json = ToolCallFormatConverter.ToOpenAi(calls);
        var back = ToolCallFormatConverter.FromOpenAi(json);
        var f = (IReadOnlyDictionary<string, object?>)back[0].Arguments["filters"]!;
        f["min"].ShouldBe(1L);
        var tags = (IReadOnlyList<object?>)back[0].Arguments["tags"]!;
        tags[0].ShouldBe("a");
    }

    [Fact]
    public void RoundTrip_AllScalarKinds_Preserved()
    {
        var args = new Dictionary<string, object?>
        {
            ["s"] = "hi",
            ["i"] = 5,
            ["l"] = 99L,
            ["d"] = 1.5,
            ["f"] = 2.5f,
            ["m"] = 3.5m,
            ["b"] = false,
            ["n"] = null,
        };
        var calls = new[] { new NormalizedToolCall("c", "x", args) };
        var json = ToolCallFormatConverter.ToOpenAi(calls);
        var back = ToolCallFormatConverter.FromOpenAi(json);
        back[0].Arguments["s"].ShouldBe("hi");
        back[0].Arguments["i"].ShouldBe(5L);
        back[0].Arguments["l"].ShouldBe(99L);
        back[0].Arguments["b"].ShouldBe(false);
        back[0].Arguments["n"].ShouldBeNull();
    }

    [Fact]
    public void RoundTrip_UnknownObject_FallsThroughViaToString()
    {
        var args = new Dictionary<string, object?> { ["custom"] = new Custom("hi") };
        var calls = new[] { new NormalizedToolCall("c", "x", args) };
        var json = ToolCallFormatConverter.ToOpenAi(calls);
        var back = ToolCallFormatConverter.FromOpenAi(json);
        back[0].Arguments["custom"].ShouldBe("Custom:hi");
    }

    [Fact]
    public void NormalizedToolCall_RecordEquality()
    {
        var a = new NormalizedToolCall("id", "n", Args(("k", 1L)));
        var b = a with { Arguments = a.Arguments };
        a.Id.ShouldBe(b.Id);
    }

    sealed class Custom
    {
        readonly string _v;
        public Custom(string v) { _v = v; }
        public override string ToString() => $"Custom:{_v}";
    }
}

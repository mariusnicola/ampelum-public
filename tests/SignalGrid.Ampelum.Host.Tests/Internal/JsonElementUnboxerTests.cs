#pragma warning disable AMPELUM_V09_PREVIEW
using System.Text.Json;
using Shouldly;
using Xunit;
using SignalGrid.Ampelum.Host.Internal;

namespace SignalGrid.Ampelum.Host.Tests.Internal;

public sealed class JsonElementUnboxerTests
{
    [Fact]
    public void Unbox_StringElement_ReturnsString()
    {
        var json = JsonSerializer.SerializeToElement("hello");
        var dict = new Dictionary<string, JsonElement> { ["key"] = json };
        var result = JsonElementUnboxer.Unbox(dict);

        result["key"].ShouldBe("hello");
    }

    [Fact]
    public void Unbox_IntegerNumber_ReturnsLong()
    {
        var json = JsonSerializer.SerializeToElement(42);
        var dict = new Dictionary<string, JsonElement> { ["key"] = json };
        var result = JsonElementUnboxer.Unbox(dict);

        result["key"].ShouldBe(42L);
    }

    [Fact]
    public void Unbox_FractionalNumber_ReturnsDouble()
    {
        var json = JsonSerializer.SerializeToElement(3.14);
        var dict = new Dictionary<string, JsonElement> { ["key"] = json };
        var result = JsonElementUnboxer.Unbox(dict);

        ((double)result["key"]!).ShouldBe(3.14, tolerance: 0.01);
    }

    [Fact]
    public void Unbox_True_ReturnsBoolTrue()
    {
        var json = JsonSerializer.SerializeToElement(true);
        var dict = new Dictionary<string, JsonElement> { ["key"] = json };
        var result = JsonElementUnboxer.Unbox(dict);

        result["key"].ShouldBe(true);
    }

    [Fact]
    public void Unbox_False_ReturnsBoolFalse()
    {
        var json = JsonSerializer.SerializeToElement(false);
        var dict = new Dictionary<string, JsonElement> { ["key"] = json };
        var result = JsonElementUnboxer.Unbox(dict);

        result["key"].ShouldBe(false);
    }

    [Fact]
    public void Unbox_Null_ReturnsNull()
    {
        var json = JsonDocument.Parse("null")!.RootElement;
        var dict = new Dictionary<string, JsonElement> { ["key"] = json };
        var result = JsonElementUnboxer.Unbox(dict);

        result["key"].ShouldBeNull();
    }

    [Fact]
    public void Unbox_NestedObject_ReturnsNestedDict()
    {
        var json = JsonDocument.Parse("{\"nested\":\"value\"}")!.RootElement;
        var dict = new Dictionary<string, JsonElement> { ["key"] = json };
        var result = JsonElementUnboxer.Unbox(dict);

        var nested = result["key"] as IReadOnlyDictionary<string, object?>;
        nested.ShouldNotBeNull();
        nested["nested"].ShouldBe("value");
    }

    [Fact]
    public void Unbox_Array_ReturnsNestedList()
    {
        var json = JsonDocument.Parse("[1, \"two\", true]")!.RootElement;
        var dict = new Dictionary<string, JsonElement> { ["key"] = json };
        var result = JsonElementUnboxer.Unbox(dict);

        var arr = result["key"] as IReadOnlyList<object?>;
        arr.ShouldNotBeNull();
        arr.Count.ShouldBe(3);
        arr[0].ShouldBe(1L);
        arr[1].ShouldBe("two");
        arr[2].ShouldBe(true);
    }

    [Fact]
    public void Unbox_UndefinedKind_ThrowsJsonException()
    {
        // default(JsonElement) has ValueKind == JsonValueKind.Undefined — hits the _ arm.
        var json = default(JsonElement);
        var dict = new Dictionary<string, JsonElement> { ["key"] = json };
        Should.Throw<JsonException>(() => JsonElementUnboxer.Unbox(dict));
    }

    [Fact]
    public void Box_String_ProducesJsonStringElement()
    {
        var dict = new Dictionary<string, object?> { ["key"] = "hello" };
        var boxed = JsonElementUnboxer.Box(dict);
        boxed["key"].GetString().ShouldBe("hello");
    }

    [Fact]
    public void Box_Long_ProducesJsonNumberElement()
    {
        var dict = new Dictionary<string, object?> { ["n"] = (long)42 };
        var boxed = JsonElementUnboxer.Box(dict);
        boxed["n"].GetInt64().ShouldBe(42L);
    }

    [Fact]
    public void Box_Null_ProducesJsonNullElement()
    {
        var dict = new Dictionary<string, object?> { ["n"] = (object?)null };
        var boxed = JsonElementUnboxer.Box(dict);
        boxed["n"].ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
    }
}

using System.Text.Json;
using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

public sealed class JsonDefaultsTests
{
    [Fact]
    public void Options_UsesCamelCase()
    {
        var json = JsonSerializer.Serialize(new { MyProp = 1 }, JsonDefaults.Options);
        json.ShouldBe("""{"myProp":1}""");
    }

    [Fact]
    public void Options_AllowReadFromString_Numbers()
    {
        var v = JsonSerializer.Deserialize<int>("\"42\"", JsonDefaults.Options);
        v.ShouldBe(42);
    }

    [Fact]
    public void Options_IsSingletonAndFrozen()
    {
        ReferenceEquals(JsonDefaults.Options, JsonDefaults.Options).ShouldBeTrue();
        JsonDefaults.Options.IsReadOnly.ShouldBeTrue();
    }
}

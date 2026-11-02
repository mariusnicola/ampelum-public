using SignalGrid.Ampelum.Internal;
using Shouldly;
using Xunit;

public sealed class NullabilityInspectorTests
{
    sealed record Sample
    {
        public required string NonNullableRef { get; init; }
        public string? NullableRef { get; init; }
        public required int NonNullableValue { get; init; }
        public int? NullableValue { get; init; }
    }

    [Theory]
    [InlineData(nameof(Sample.NonNullableRef), false)]
    [InlineData(nameof(Sample.NullableRef), true)]
    [InlineData(nameof(Sample.NonNullableValue), false)]
    [InlineData(nameof(Sample.NullableValue), true)]
    public void IsNullable(string propertyName, bool expected)
    {
        var prop = typeof(Sample).GetProperty(propertyName)!;
        NullabilityInspector.IsNullable(prop).ShouldBe(expected);
    }
}

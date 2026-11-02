using SignalGrid.Ampelum.Internal;
using Shouldly;
using Xunit;

public sealed class HumanizeNameTests
{
    [Theory]
    [InlineData("Question", "Question")]
    [InlineData("FirstName", "First Name")]
    [InlineData("URLField", "URL Field")]
    [InlineData("HTTPRequest", "HTTP Request")]
    [InlineData("iPhone", "i Phone")]
    [InlineData("A", "A")]
    [InlineData("ABC", "ABC")]
    public void Split(string input, string expected) =>
        HumanizeName.Split(input).ShouldBe(expected);
}

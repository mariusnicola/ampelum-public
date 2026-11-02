using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

public sealed class FakeEmbeddingGeneratorTests
{
    [Fact]
    public async Task GenerateAsync_ReturnsSingleEmbedding_MatchingInput()
    {
        var scripted = new Dictionary<string, float[]>
        {
            ["hello"] = [1f, 0f, 0f],
            ["world"] = [0f, 1f, 0f],
        };
        var gen = new FakeEmbeddingGenerator(scripted, fallbackDimensions: 3);

        var result = await gen.GenerateAsync(["hello"], cancellationToken: TestContext.Current.CancellationToken);
        result.Count.ShouldBe(1);
        result[0].Vector.ToArray().ShouldBe(new[] { 1f, 0f, 0f });
    }

    [Fact]
    public async Task GenerateAsync_UnrecognisedInput_ReturnsFallbackZeroVector()
    {
        var gen = new FakeEmbeddingGenerator(new Dictionary<string, float[]>(), fallbackDimensions: 4);
        var result = await gen.GenerateAsync(["unknown"], cancellationToken: TestContext.Current.CancellationToken);
        result[0].Vector.ToArray().ShouldBe(new float[4]);
    }

    [Fact]
    public async Task GenerateAsync_MultipleInputs_ReturnsOneEmbeddingEach()
    {
        var scripted = new Dictionary<string, float[]>
        {
            ["a"] = [1f, 0f],
            ["b"] = [0f, 1f],
        };
        var gen = new FakeEmbeddingGenerator(scripted, fallbackDimensions: 2);
        var result = await gen.GenerateAsync(["a", "b"], cancellationToken: TestContext.Current.CancellationToken);
        result.Count.ShouldBe(2);
        result[0].Vector.ToArray().ShouldBe(new[] { 1f, 0f });
        result[1].Vector.ToArray().ShouldBe(new[] { 0f, 1f });
    }

    [Fact]
    public void Metadata_ReturnsExpectedProviderName()
    {
        var gen = new FakeEmbeddingGenerator(new Dictionary<string, float[]>(), fallbackDimensions: 2);
        gen.Metadata.ProviderName.ShouldBe(nameof(FakeEmbeddingGenerator));
    }

    [Fact]
    public void Constructor_NullScriptedDict_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            new FakeEmbeddingGenerator(null!, fallbackDimensions: 2));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var gen = new FakeEmbeddingGenerator(new Dictionary<string, float[]>(), fallbackDimensions: 2);
        gen.Dispose();
        gen.Dispose();
    }

    [Fact]
    public void GetService_ReturnsNull()
    {
        var gen = new FakeEmbeddingGenerator(new Dictionary<string, float[]>(), fallbackDimensions: 2);
        gen.GetService(typeof(string)).ShouldBeNull();
    }
}

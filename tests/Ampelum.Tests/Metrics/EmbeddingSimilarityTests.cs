using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Metrics;
using SignalGrid.Ampelum.Testing;
using Shouldly;
using Xunit;

public sealed class EmbeddingSimilarityTests
{
    sealed record QA : ISignature
    {
        [Input] public string Question { get; init; } = "";
        [Output] public string Answer { get; init; } = "";
    }

    static readonly SignatureMetadata Meta = SignatureMetadataCache.For<QA>();

    static FakeEmbeddingGenerator Embedder(params (string text, float[] vec)[] entries) =>
        new(entries.ToDictionary(e => e.text, e => e.vec), fallbackDimensions: 2);

    [Fact]
    public void Identical_Vectors_Returns_One()
    {
        var emb = Embedder(("hello", new float[] { 1, 0 }));
        new EmbeddingSimilarity<QA>("Answer", emb)
            .Score(new QA { Answer = "hello" }, new QA { Answer = "hello" }, Meta)
            .ShouldBe(1.0, 1e-6);
    }

    [Fact]
    public void Orthogonal_Vectors_Returns_Zero()
    {
        var emb = Embedder(("a", new float[] { 1, 0 }), ("b", new float[] { 0, 1 }));
        new EmbeddingSimilarity<QA>("Answer", emb)
            .Score(new QA { Answer = "a" }, new QA { Answer = "b" }, Meta)
            .ShouldBe(0.0, 1e-6);
    }

    [Fact]
    public void Negative_Cosine_Clamped_To_Zero()
    {
        var emb = Embedder(("a", new float[] { 1, 0 }), ("b", new float[] { -1, 0 }));
        new EmbeddingSimilarity<QA>("Answer", emb)
            .Score(new QA { Answer = "a" }, new QA { Answer = "b" }, Meta)
            .ShouldBe(0.0);
    }

    [Fact]
    public void Threshold_Above_Returns_One()
    {
        var emb = Embedder(("a", new float[] { 1, 1 }), ("b", new float[] { 1, 0 }));
        // cos = 1/sqrt(2) ~= 0.707
        new EmbeddingSimilarity<QA>("Answer", emb, threshold: 0.5)
            .Score(new QA { Answer = "a" }, new QA { Answer = "b" }, Meta)
            .ShouldBe(1.0);
    }

    [Fact]
    public void Threshold_Below_Returns_Zero()
    {
        var emb = Embedder(("a", new float[] { 1, 1 }), ("b", new float[] { 1, 0 }));
        new EmbeddingSimilarity<QA>("Answer", emb, threshold: 0.9)
            .Score(new QA { Answer = "a" }, new QA { Answer = "b" }, Meta)
            .ShouldBe(0.0);
    }

    [Fact]
    public void BothEmpty_Returns_One_NoEmbedderCall()
    {
        var emb = Embedder();
        new EmbeddingSimilarity<QA>("Answer", emb)
            .Score(new QA(), new QA(), Meta).ShouldBe(1.0);
    }

    [Fact]
    public void OneEmpty_Returns_Zero()
    {
        var emb = Embedder();
        new EmbeddingSimilarity<QA>("Answer", emb)
            .Score(new QA { Answer = "x" }, new QA(), Meta).ShouldBe(0.0);
    }

    [Fact]
    public void OtherEmpty_Returns_Zero()
    {
        var emb = Embedder();
        new EmbeddingSimilarity<QA>("Answer", emb)
            .Score(new QA(), new QA { Answer = "x" }, Meta).ShouldBe(0.0);
    }

    [Fact]
    public void Threshold_OutOfRange_Throws()
    {
        var emb = Embedder();
        Should.Throw<ArgumentOutOfRangeException>(() => new EmbeddingSimilarity<QA>("Answer", emb, threshold: 1.5));
        Should.Throw<ArgumentOutOfRangeException>(() => new EmbeddingSimilarity<QA>("Answer", emb, threshold: -0.1));
    }

    [Fact]
    public void Null_Embedder_Throws() =>
        Should.Throw<ArgumentNullException>(() => new EmbeddingSimilarity<QA>("Answer", null!));

    [Fact]
    public void Empty_Field_Throws() =>
        Should.Throw<ArgumentException>(() => new EmbeddingSimilarity<QA>("", Embedder()));

    [Fact]
    public void Name_NoThreshold() =>
        new EmbeddingSimilarity<QA>("Answer", Embedder()).Name.ShouldBe("embed_sim[Answer]");

    [Fact]
    public void Name_WithThreshold() =>
        new EmbeddingSimilarity<QA>("Answer", Embedder(), threshold: 0.85).Name.ShouldContain("embed_sim[Answer,");

    [Fact]
    public void FieldMissing_Throws()
    {
        var emb = Embedder(("a", new float[] { 1, 0 }));
        var m = new EmbeddingSimilarity<QA>("Missing", emb);
        Should.Throw<ArgumentException>(() => m.Score(new QA { Answer = "a" }, new QA { Answer = "a" }, Meta));
    }

    [Fact]
    public void Zero_Vector_Returns_Zero()
    {
        var emb = Embedder(("a", new float[] { 0, 0 }), ("b", new float[] { 1, 0 }));
        new EmbeddingSimilarity<QA>("Answer", emb)
            .Score(new QA { Answer = "a" }, new QA { Answer = "b" }, Meta).ShouldBe(0.0);
    }
}

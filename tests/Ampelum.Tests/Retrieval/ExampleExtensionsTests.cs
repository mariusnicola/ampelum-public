using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Retrieval;
using Shouldly;
using Xunit;

public sealed class ExampleExtensionsTests
{
    sealed record QA : ISignature
    {
        [Input]  public string Question { get; init; } = "";
        [Output] public string Answer   { get; init; } = "";
    }

    static RetrievedDocument DocWithMeta(string question) =>
        new("doc1", question, 0.9,
            new Dictionary<string, object?> { ["Question"] = question });

    [Fact]
    public void ToExample_MapsInputFieldsFromMetadata()
    {
        var doc = DocWithMeta("What is 2+2?");
        var outputs = new QA { Question = "What is 2+2?", Answer = "4" };

        var example = doc.ToExample<QA>(outputs);

        example.Inputs["Question"].ShouldBe("What is 2+2?");
        example.Outputs["Answer"].ShouldBe("4");
    }

    [Fact]
    public void ToExample_MissingMetadataField_SilentlyOmitted()
    {
        // Metadata has no "Question" key → Inputs should be empty (silently omitted).
        var doc = new RetrievedDocument("d1", "text", 0.5, new Dictionary<string, object?>());
        var outputs = new QA { Answer = "42" };

        var example = doc.ToExample<QA>(outputs);

        example.Inputs.ContainsKey("Question").ShouldBeFalse();
        example.Outputs["Answer"].ShouldBe("42");
    }

    [Fact]
    public void ToExample_ReturnsCorrectExample_RoundTrip()
    {
        var doc = DocWithMeta("cap of France?");
        var outputs = new QA { Question = "cap of France?", Answer = "Paris" };

        var example = doc.ToExample<QA>(outputs);
        var meta = SignatureMetadataCache.For<QA>();

        example.Inputs.Keys.ShouldBe(meta.Inputs
            .Where(f => doc.Metadata.ContainsKey(f.Name))
            .Select(f => f.Name), ignoreOrder: true);
    }

    [Fact]
    public void ToExample_NullDocument_ThrowsArgumentNullException()
    {
        RetrievedDocument doc = null!;
        var outputs = new QA { Question = "Q", Answer = "A" };

        var ex = Should.Throw<ArgumentNullException>(() =>
            doc.ToExample<QA>(outputs));

        ex.ParamName.ShouldBe("doc");
    }

    [Fact]
    public void ToExample_NullOutputs_ThrowsArgumentNullException()
    {
        var doc = DocWithMeta("Q");
        QA outputs = null!;

        var ex = Should.Throw<ArgumentNullException>(() =>
            doc.ToExample<QA>(outputs));

        ex.ParamName.ShouldBe("outputs");
    }

    [Fact]
    public void ToExample_OutputFieldsFullyReflected()
    {
        var doc = DocWithMeta("What is 2+2?");
        var outputs = new QA { Question = "What is 2+2?", Answer = "4" };

        var example = doc.ToExample<QA>(outputs);

        example.Outputs.ContainsKey("Answer").ShouldBeTrue();
        example.Outputs["Answer"].ShouldBe("4");
    }
}

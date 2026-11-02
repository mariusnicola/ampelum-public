using SignalGrid.Ampelum.Retrieval;
using Shouldly;
using Xunit;

public sealed class RetrievedDocumentTests
{
    static RetrievedDocument MakeDoc(string id = "d1", string text = "hello", double score = 0.9) =>
        new(id, text, score, new Dictionary<string, object?> { ["source"] = "wiki" });

    [Fact]
    public void Construct_SetsAllProperties()
    {
        var doc = MakeDoc();
        doc.Id.ShouldBe("d1");
        doc.Text.ShouldBe("hello");
        doc.Score.ShouldBe(0.9);
        doc.Metadata["source"].ShouldBe("wiki");
    }

    [Fact]
    public void Record_SupportsValueEquality_OnSharedReferences()
    {
        // Note: record equality uses reference equality on the IReadOnlyDictionary field
        // (default record semantics for reference-typed fields). Two records share a dict
        // instance => equal; separate dicts => unequal even with identical content.
        var meta = new Dictionary<string, object?> { ["source"] = "wiki" };
        var a = new RetrievedDocument("d1", "hello", 0.9, meta);
        var b = new RetrievedDocument("d1", "hello", 0.9, meta);
        a.ShouldBe(b);
    }

    [Fact]
    public void Metadata_IsIReadOnlyDictionary()
    {
        var doc = MakeDoc();
        doc.Metadata.ShouldBeAssignableTo<IReadOnlyDictionary<string, object?>>();
    }

    [Fact]
    public void With_Expression_ProducesModifiedCopy()
    {
        var a = MakeDoc();
        var b = a with { Score = 0.5 };
        b.Score.ShouldBe(0.5);
        a.Score.ShouldBe(0.9);
    }

    [Fact]
    public void ToString_IncludesTypeName()
    {
        MakeDoc().ToString().ShouldContain("RetrievedDocument");
    }
}

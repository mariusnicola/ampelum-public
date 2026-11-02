using System.Text.Json;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Evaluation;
using SignalGrid.Ampelum.Metrics;
using SignalGrid.Ampelum.Retrieval.Evaluation;
using SignalGrid.Ampelum.Testing;
using Shouldly;
using Xunit;

/// <summary>Targeted tests closing residual branch-coverage gaps in v0.9d additions.</summary>
public sealed class CoverageGapV09dTests
{
    sealed record QN : ISignature
    {
        [Input] public string Q { get; init; } = "";
        [Output] public string? A { get; init; }
    }

    static readonly SignatureMetadata QnMeta = SignatureMetadataCache.For<QN>();

    [Fact]
    public void RougeN_Null_Field_Value_Treated_As_Empty() =>
        new RougeN<QN>(1, "A").Score(new QN { A = null }, new QN { A = null }, QnMeta).ShouldBe(1.0);

    [Fact]
    public void RougeL_Null_Field_Value_Treated_As_Empty() =>
        new RougeL<QN>("A").Score(new QN { A = null }, new QN { A = null }, QnMeta).ShouldBe(1.0);

    [Fact]
    public void Bleu_Null_Field_Value_Treated_As_Empty() =>
        new Bleu<QN>(1, "A").Score(new QN { A = null }, new QN { A = null }, QnMeta).ShouldBe(1.0);

    [Fact]
    public void EmbeddingSimilarity_Null_Field_Value_Treated_As_Empty()
    {
        var e = new FakeEmbeddingGenerator(new Dictionary<string, float[]>(), 2);
        new EmbeddingSimilarity<QN>("A", e).Score(new QN { A = null }, new QN { A = null }, QnMeta).ShouldBe(1.0);
    }

    [Fact]
    public void RougeN_Duplicate_Reference_Tokens_Counted()
    {
        // reference has duplicate "the" -> exercises CountOverlap dict-update branch
        new RougeN<QN>(1, "A").Score(
            new QN { A = "the the the" },
            new QN { A = "the the cat" },
            QnMeta).ShouldBe(2.0 / 3.0, 1e-9);
    }

    [Fact]
    public void NDCGAtK_Gold_Larger_Than_K_Ideal_Capped()
    {
        // 3 gold, k=2 -> ideal capped to 2
        var v = new NDCGAtK(2).Compute(new[] { "a", "b" }, new[] { "a", "b", "c" });
        v.ShouldBe(1.0, 1e-9);
    }

    [Fact]
    public void NDCGAtK_K_Override_Used_Instead_Of_Default() =>
        new NDCGAtK(10).Compute(new[] { "x", "a" }, new[] { "a" }, k: 1).ShouldBe(0.0);

    [Fact]
    public void EvalReport_NoFailures_NoSampleTable()
    {
        var ex = new LabeledExample<QN>(new QN(), new QN());
        var r = new EvaluationResult<QN>(new[] { new ExampleScore<QN>(ex, null, 1.0, null) },
            1.0, 0, 0, Array.Empty<ExampleScore<QN>>());
        var (md, _) = EvalReport.Render(r, new[] { (IMetric)new ExactMatch<QN>("A") });
        md.ShouldNotContain("Failure samples");
    }

    [Fact]
    public void EvalReport_FailureSample_Without_Error_JsonNull()
    {
        var ex = new LabeledExample<QN>(new QN(), new QN());
        var s = new ExampleScore<QN>(ex, null, 0.0, null);
        var r = new EvaluationResult<QN>(new[] { s }, 0.0, 0, 0, new[] { s });
        var (_, json) = EvalReport.Render(r, new[] { (IMetric)new ExactMatch<QN>("A") });
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("failureSamples")[0].GetProperty("error").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void EvalComparisonReport_LargeN_PValue_AWins()
    {
        var rA = MakeResult(Enumerable.Repeat(0.9, 50).ToArray());
        var rB = MakeResult(Enumerable.Repeat(0.5, 50).ToArray());
        var (md, _) = EvalComparisonReport.Render("A", rA, "B", rB);
        md.ShouldContain("A wins");
    }

    [Fact]
    public void EvalComparisonReport_SmallN_NS()
    {
        var rA = MakeResult(new[] { 0.5, 0.5, 0.5 });
        var rB = MakeResult(new[] { 0.5, 0.5, 0.5 });
        var (md, _) = EvalComparisonReport.Render("A", rA, "B", rB, randomSeed: 1);
        md.ShouldContain("delta ns");
    }

    static EvaluationResult<QN> MakeResult(double[] scores)
    {
        var ex = new LabeledExample<QN>(new QN(), new QN());
        var arr = scores.Select(s => new ExampleScore<QN>(ex, null, s, null)).ToArray();
        return new EvaluationResult<QN>(arr, scores.Length == 0 ? 0 : scores.Average(), 0, 0, Array.Empty<ExampleScore<QN>>());
    }

    [Fact]
    public void RetrievalEvalHarness_ParseIds_Scalar_NonString_NonEnumerable() =>
        RetrievalEvalHarness.ParseIds(42L).ShouldBe(new[] { "42" });

    [Fact]
    public void RetrievalEvalHarness_ParseIds_Empty_String_Csv() =>
        RetrievalEvalHarness.ParseIds("").ShouldBeEmpty();
}

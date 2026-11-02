using System.Text.Json;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Evaluation;
using Shouldly;
using Xunit;

public sealed class EvalComparisonReportTests
{
    sealed record QA : ISignature
    {
        [Input] public string Q { get; init; } = "";
        [Output] public string A { get; init; } = "";
    }

    static EvaluationResult<QA> Make(double[] scores)
    {
        var ex = new LabeledExample<QA>(new QA(), new QA());
        var arr = scores.Select(s => new ExampleScore<QA>(ex, null, s, null)).ToArray();
        var mean = scores.Length == 0 ? 0 : scores.Average();
        return new EvaluationResult<QA>(arr, mean, 0, 0, Array.Empty<ExampleScore<QA>>());
    }

    [Fact]
    public void Render_Different_Counts_Throws() =>
        Should.Throw<ArgumentException>(() => EvalComparisonReport.Render("A", Make(new[] { 1.0 }), "B", Make(new[] { 1.0, 0.0 })));

    [Fact]
    public void Render_Null_Or_Empty_Labels_Throw()
    {
        Should.Throw<ArgumentException>(() => EvalComparisonReport.Render("", Make(new[] { 1.0 }), "B", Make(new[] { 1.0 })));
        Should.Throw<ArgumentException>(() => EvalComparisonReport.Render("A", Make(new[] { 1.0 }), "", Make(new[] { 1.0 })));
    }

    [Fact]
    public void Render_Null_Results_Throw()
    {
        Should.Throw<ArgumentNullException>(() => EvalComparisonReport.Render("A", null!, "B", Make(new[] { 1.0 })));
        Should.Throw<ArgumentNullException>(() => EvalComparisonReport.Render("A", Make(new[] { 1.0 }), "B", null!));
    }

    [Fact]
    public void Render_LargeN_BWins_Reports_PValue()
    {
        var a = Make(Enumerable.Range(0, 100).Select(_ => 0.5).ToArray());
        var b = Make(Enumerable.Range(0, 100).Select(_ => 0.9).ToArray());
        var (md, _) = EvalComparisonReport.Render("A", a, "B", b);
        md.ShouldContain("B wins");
        md.ShouldContain("paired-t");
    }

    [Fact]
    public void Render_LargeN_NoSignificance_Reports_NS()
    {
        var rng = new Random(123);
        var a = Make(Enumerable.Range(0, 100).Select(_ => rng.NextDouble()).ToArray());
        var rng2 = new Random(123);
        var b = Make(Enumerable.Range(0, 100).Select(_ => rng2.NextDouble()).ToArray());
        var (md, _) = EvalComparisonReport.Render("A", a, "B", b);
        md.ShouldContain("delta ns");
    }

    [Fact]
    public void Render_SmallN_Bootstrap_With_Seed_Deterministic()
    {
        var a = Make(new[] { 0.1, 0.2, 0.3 });
        var b = Make(new[] { 0.5, 0.6, 0.7 });
        var (md1, _) = EvalComparisonReport.Render("A", a, "B", b, randomSeed: 42);
        var (md2, _) = EvalComparisonReport.Render("A", a, "B", b, randomSeed: 42);
        md1.ShouldBe(md2);
    }

    [Fact]
    public void Render_SmallN_NoOverlap_BWins()
    {
        var a = Make(new[] { 0.0, 0.1, 0.05, 0.1, 0.0 });
        var b = Make(new[] { 0.9, 0.95, 0.9, 1.0, 0.85 });
        var (md, _) = EvalComparisonReport.Render("A", a, "B", b, randomSeed: 1);
        md.ShouldContain("B wins");
    }

    [Fact]
    public void Render_SmallN_AWins()
    {
        var a = Make(new[] { 0.9, 0.95, 0.9, 1.0, 0.85 });
        var b = Make(new[] { 0.0, 0.1, 0.05, 0.1, 0.0 });
        var (md, _) = EvalComparisonReport.Render("A", a, "B", b, randomSeed: 1);
        md.ShouldContain("A wins");
    }

    [Fact]
    public void Render_LargeN_AWins_Reports_PValue()
    {
        var a = Make(Enumerable.Range(0, 100).Select(_ => 0.9).ToArray());
        var b = Make(Enumerable.Range(0, 100).Select(_ => 0.5).ToArray());
        var (md, _) = EvalComparisonReport.Render("A", a, "B", b);
        md.ShouldContain("A wins");
    }

    [Fact]
    public void Render_LargeN_All_Equal_NS_With_SE_Zero()
    {
        var a = Make(Enumerable.Range(0, 30).Select(_ => 0.5).ToArray());
        var b = Make(Enumerable.Range(0, 30).Select(_ => 0.5).ToArray());
        var (md, _) = EvalComparisonReport.Render("A", a, "B", b);
        md.ShouldContain("delta ns");
    }

    [Fact]
    public void Render_Json_Round_Trips()
    {
        var a = Make(new[] { 0.1, 0.2 });
        var b = Make(new[] { 0.3, 0.4 });
        var (_, json) = EvalComparisonReport.Render("A", a, "B", b, randomSeed: 1);
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("labelA").GetString().ShouldBe("A");
        doc.RootElement.GetProperty("n").GetInt32().ShouldBe(2);
    }

    [Fact]
    public void Render_Includes_LabelA_LabelB_In_Header()
    {
        var (md, _) = EvalComparisonReport.Render("Foo", Make(new[] { 0.5 }), "Bar", Make(new[] { 0.5 }), randomSeed: 1);
        md.ShouldContain("Foo");
        md.ShouldContain("Bar");
    }
}

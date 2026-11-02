using System.Text.Json;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Evaluation;
using SignalGrid.Ampelum.Metrics;
using Shouldly;
using Xunit;

public sealed class EvalReportTests
{
    sealed record QA : ISignature
    {
        [Input] public string Q { get; init; } = "";
        [Output] public string A { get; init; } = "";
    }

    static EvaluationResult<QA> Empty() => new(Array.Empty<ExampleScore<QA>>(), 0, 0, 0, Array.Empty<ExampleScore<QA>>());

    static EvaluationResult<QA> WithFailures()
    {
        var ex = new LabeledExample<QA>(new QA(), new QA());
        var s1 = new ExampleScore<QA>(ex, null, 0.0, new InvalidOperationException("boom"));
        var s2 = new ExampleScore<QA>(ex, null, 0.5, null);
        return new EvaluationResult<QA>(new[] { s1, s2 }, 0.25, 0.25, 1, new[] { s1 });
    }

    sealed class Untyped : IMetric { public double Score(object a, object e, SignatureMetadata m) => 0; }

    [Fact]
    public void Render_Empty_Result_Has_Headers_Zero_Counts()
    {
        var (md, _) = EvalReport.Render(Empty(), new[] { (IMetric)new ExactMatch<QA>("A") });
        md.ShouldContain("# Evaluation Report");
        md.ShouldContain("| Examples | 0 |");
        md.ShouldContain("| Mean | 0.0000 |");
    }

    [Fact]
    public void Render_With_Failures_Shows_Sample_Table()
    {
        var (md, _) = EvalReport.Render(WithFailures(), new[] { (IMetric)new ExactMatch<QA>("A") });
        md.ShouldContain("Failure samples");
        md.ShouldContain("boom");
    }

    [Fact]
    public void Render_Uses_INamedMetric_Name_When_Present()
    {
        var (md, _) = EvalReport.Render(Empty(), new[] { (IMetric)new ExactMatch<QA>("A") });
        md.ShouldContain("exact_match[A]");
    }

    [Fact]
    public void Render_Falls_Back_To_TypeName_For_Untyped_Metric()
    {
        var (md, _) = EvalReport.Render(Empty(), new[] { (IMetric)new Untyped() });
        md.ShouldContain("Untyped");
    }

    [Fact]
    public void Render_NullArgs_Throws()
    {
        Should.Throw<ArgumentNullException>(() => EvalReport.Render<QA>(null!, new[] { (IMetric)new ExactMatch<QA>("A") }));
        Should.Throw<ArgumentNullException>(() => EvalReport.Render(Empty(), null!));
    }

    [Fact]
    public void Render_Json_Round_Trips()
    {
        var (_, json) = EvalReport.Render(WithFailures(), new[] { (IMetric)new ExactMatch<QA>("A") });
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("examples").GetInt32().ShouldBe(2);
        doc.RootElement.GetProperty("failures").GetInt32().ShouldBe(1);
    }

    [Fact]
    public void Render_Markdown_Contains_Mean_To_Four_Decimals()
    {
        var (md, _) = EvalReport.Render(WithFailures(), new[] { (IMetric)new ExactMatch<QA>("A") });
        md.ShouldContain("0.2500");
    }
}

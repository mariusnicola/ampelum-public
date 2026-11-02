using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Metrics;
using SignalGrid.Ampelum.Testing;
using Shouldly;
using Xunit;

public sealed class INamedMetricTests
{
    sealed record QA : ISignature
    {
        [Input] public string Question { get; init; } = "";
        [Output] public string Answer { get; init; } = "";
    }

    [Fact]
    public void ExactMatch_Name_Includes_FieldName() =>
        new ExactMatch<QA>("Answer").Name.ShouldBe("exact_match[Answer]");

    [Fact]
    public void F1_Name_Includes_FieldName() =>
        new F1<QA>("Answer").Name.ShouldBe("f1[Answer]");

    [Fact]
    public void LLMAsJudgeMetric_Name_Is_Stable()
    {
        var chat = new FakeChatClient();
        new LLMAsJudgeMetric<QA>(chat, "Judge").Name.ShouldBe("llm_as_judge");
    }

    [Fact]
    public void INamedMetric_Is_IMetric()
    {
        IMetric m = new ExactMatch<QA>("Answer");
        m.ShouldBeAssignableTo<INamedMetric>();
        ((INamedMetric)m).Name.ShouldBe("exact_match[Answer]");
    }
}

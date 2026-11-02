using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Metrics;
using SignalGrid.Ampelum.Testing;
using Shouldly;
using Xunit;

public sealed class LLMAsJudgeMetricSamplesTests
{
    sealed record QA : ISignature
    {
        [Input] public string Q { get; init; } = "";
        [Output] public string A { get; init; } = "";
    }

    static readonly SignatureMetadata Meta = SignatureMetadataCache.For<QA>();

    static string J(double s) => $"{{\"score\": {s.ToString(System.Globalization.CultureInfo.InvariantCulture)}, \"reasoning\": \"\"}}";

    [Fact]
    public void Samples_One_Behaves_Like_Legacy_Ctor()
    {
        var chat = new FakeChatClient().EnqueueText(J(0.7));
        var m = new LLMAsJudgeMetric<QA>(chat, "Judge",
            new LLMAsJudgeOptions(Samples: 1, Aggregation: JudgeAggregation.Median));
        m.Score(new QA(), new QA(), Meta).ShouldBe(0.7);
    }

    [Fact]
    public void Median_Of_Three_Returns_Middle()
    {
        var chat = new FakeChatClient().EnqueueText(J(0.1)).EnqueueText(J(0.5)).EnqueueText(J(0.9));
        var m = new LLMAsJudgeMetric<QA>(chat, "Judge",
            new LLMAsJudgeOptions(Samples: 3, Aggregation: JudgeAggregation.Median));
        m.Score(new QA(), new QA(), Meta).ShouldBe(0.5);
    }

    [Fact]
    public void Median_Even_N_Interpolated()
    {
        var chat = new FakeChatClient().EnqueueText(J(0.2)).EnqueueText(J(0.4)).EnqueueText(J(0.6)).EnqueueText(J(0.8));
        var m = new LLMAsJudgeMetric<QA>(chat, "Judge",
            new LLMAsJudgeOptions(Samples: 4, Aggregation: JudgeAggregation.Median));
        m.Score(new QA(), new QA(), Meta).ShouldBe(0.5, 1e-9);
    }

    [Fact]
    public void Mean_Aggregation_Returns_Average()
    {
        var chat = new FakeChatClient().EnqueueText(J(0.0)).EnqueueText(J(1.0));
        var m = new LLMAsJudgeMetric<QA>(chat, "Judge",
            new LLMAsJudgeOptions(Samples: 2, Aggregation: JudgeAggregation.Mean));
        m.Score(new QA(), new QA(), Meta).ShouldBe(0.5);
    }

    [Fact]
    public void MajorityVote_Two_High_One_Low_Returns_One()
    {
        var chat = new FakeChatClient().EnqueueText(J(0.9)).EnqueueText(J(0.95)).EnqueueText(J(0.0));
        var m = new LLMAsJudgeMetric<QA>(chat, "Judge",
            new LLMAsJudgeOptions(Samples: 3, Aggregation: JudgeAggregation.MajorityVote));
        m.Score(new QA(), new QA(), Meta).ShouldBe(1.0);
    }

    [Fact]
    public void MajorityVote_Mid_Bucket_Wins()
    {
        var chat = new FakeChatClient().EnqueueText(J(0.5)).EnqueueText(J(0.6)).EnqueueText(J(0.0));
        var m = new LLMAsJudgeMetric<QA>(chat, "Judge",
            new LLMAsJudgeOptions(Samples: 3, Aggregation: JudgeAggregation.MajorityVote));
        m.Score(new QA(), new QA(), Meta).ShouldBe(0.5);
    }

    [Fact]
    public void MajorityVote_Low_Bucket_Wins()
    {
        var chat = new FakeChatClient().EnqueueText(J(0.0)).EnqueueText(J(0.1)).EnqueueText(J(0.9));
        var m = new LLMAsJudgeMetric<QA>(chat, "Judge",
            new LLMAsJudgeOptions(Samples: 3, Aggregation: JudgeAggregation.MajorityVote));
        m.Score(new QA(), new QA(), Meta).ShouldBe(0.0);
    }

    [Fact]
    public void Samples_Zero_Throws()
    {
        var chat = new FakeChatClient();
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new LLMAsJudgeMetric<QA>(chat, "Judge", new LLMAsJudgeOptions(Samples: 0)));
    }

    [Fact]
    public void Options_Null_Throws() =>
        Should.Throw<ArgumentNullException>(() =>
            new LLMAsJudgeMetric<QA>(new FakeChatClient(), "Judge", (LLMAsJudgeOptions)null!));

    [Fact]
    public void Samples_Five_Calls_Judge_Five_Times()
    {
        var chat = new FakeChatClient();
        for (var i = 0; i < 5; i++) chat.EnqueueText(J(0.5));
        var m = new LLMAsJudgeMetric<QA>(chat, "Judge",
            new LLMAsJudgeOptions(Samples: 5, Aggregation: JudgeAggregation.Mean));
        m.Score(new QA(), new QA(), Meta).ShouldBe(0.5, 1e-9);
    }

    [Fact]
    public void Aggregate_Unknown_Enum_Throws()
    {
        var chat = new FakeChatClient().EnqueueText(J(0.5)).EnqueueText(J(0.5));
        var m = new LLMAsJudgeMetric<QA>(chat, "Judge",
            new LLMAsJudgeOptions(Samples: 2, Aggregation: (JudgeAggregation)99));
        Should.Throw<ArgumentOutOfRangeException>(() => m.Score(new QA(), new QA(), Meta));
    }

    [Fact]
    public void Name_Is_llm_as_judge() =>
        new LLMAsJudgeMetric<QA>(new FakeChatClient(), "Judge", new LLMAsJudgeOptions()).Name.ShouldBe("llm_as_judge");
}

using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Budget;
using SignalGrid.Ampelum.Metrics;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

public sealed class EvaluateBudgetTests
{
    sealed record QA : ISignature
    {
        [Input] public string Question { get; init; } = "";
        [Output] public string Answer { get; init; } = "";
    }

    sealed class CountingBudget : IBudget
    {
        public int CallsRecorded;
        public long TokensRecorded;
        public int? CallLimit { get; init; }
        public long? TokenLimit => null;
        public decimal? CostCentsLimit => null;
        public DateTimeOffset? Deadline => null;

        public void Consume(long tokens, decimal costCents, int calls = 1)
        {
            var newCalls = Interlocked.Add(ref CallsRecorded, calls);
            Interlocked.Add(ref TokensRecorded, tokens);
            if (CallLimit is { } lim && newCalls > lim)
                throw new BudgetExceededException(Snapshot());
        }

        public BudgetSnapshot Snapshot() =>
            new(TokensRecorded, null, 0m, null, CallsRecorded, CallLimit, null);
    }

    static TraceRecord T(int? inTok = null, int? outTok = null)
    {
        var usage = inTok is null && outTok is null ? null : new UsageDetails { InputTokenCount = inTok, OutputTokenCount = outTok };
        return new TraceRecord(Guid.NewGuid(), "Test", typeof(QA),
            new Dictionary<string, object?>(), new Dictionary<string, object?>(),
            Array.Empty<ChatMessage>(), "", usage, TimeSpan.Zero, null, new Dictionary<string, object?>());
    }

    static Func<QA, CancellationToken, Task<Prediction<QA>>> OkProgram(int? inTok = null, int? outTok = null) =>
        (q, _) => Task.FromResult(new Prediction<QA>(new QA { Question = q.Question, Answer = "x" }, T(inTok, outTok)));

    static Func<QA, CancellationToken, Task<Prediction<QA>>> ThrowingProgram() =>
        (q, _) => throw new InvalidOperationException("boom");

    static IReadOnlyList<LabeledExample<QA>> Examples(int n) =>
        Enumerable.Range(0, n).Select(i => new LabeledExample<QA>(new QA { Question = $"q{i}" }, new QA { Answer = "x" })).ToList();

    [Fact]
    public async Task Budget_Null_Default_NoConsume()
    {
        var ev = new Evaluate<QA>(OkProgram(10, 20), new ExactMatch<QA>("Answer"));
        var r = await ev.RunAsync(Examples(3), TestContext.Current.CancellationToken);
        r.Mean.ShouldBe(1.0);
    }

    [Fact]
    public async Task Budget_Within_Limits_Records_All()
    {
        var b = new CountingBudget();
        var ev = new Evaluate<QA>(OkProgram(10, 20), new ExactMatch<QA>("Answer"), 1, 10, null, b);
        var r = await ev.RunAsync(Examples(3), TestContext.Current.CancellationToken);
        r.Mean.ShouldBe(1.0);
        b.CallsRecorded.ShouldBe(3);
        b.TokensRecorded.ShouldBe(90);
    }

    [Fact]
    public async Task Budget_Exceeded_Surfaces_BudgetExceededException()
    {
        var b = new CountingBudget { CallLimit = 1 };
        var ev = new Evaluate<QA>(OkProgram(10, 20), new ExactMatch<QA>("Answer"), 1, 10, null, b);
        await Should.ThrowAsync<BudgetExceededException>(() => ev.RunAsync(Examples(5)));
    }

    [Fact]
    public async Task Budget_Consume_Called_Once_Per_Successful_Prediction()
    {
        var b = new CountingBudget();
        var ev = new Evaluate<QA>(OkProgram(1, 1), new ExactMatch<QA>("Answer"), 1, 10, null, b);
        await ev.RunAsync(Examples(4), TestContext.Current.CancellationToken);
        b.CallsRecorded.ShouldBe(4);
    }

    [Fact]
    public async Task Budget_Not_Called_When_Program_Throws()
    {
        var b = new CountingBudget();
        var ev = new Evaluate<QA>(ThrowingProgram(), new ExactMatch<QA>("Answer"), 1, 10, null, b);
        var r = await ev.RunAsync(Examples(2), TestContext.Current.CancellationToken);
        r.FailureCount.ShouldBe(2);
        b.CallsRecorded.ShouldBe(0);
    }

    [Fact]
    public async Task Budget_Null_Usage_Records_Zero_Tokens()
    {
        var b = new CountingBudget();
        var ev = new Evaluate<QA>(OkProgram(null, null), new ExactMatch<QA>("Answer"), 1, 10, null, b);
        await ev.RunAsync(Examples(2), TestContext.Current.CancellationToken);
        b.CallsRecorded.ShouldBe(2);
        b.TokensRecorded.ShouldBe(0);
    }
}

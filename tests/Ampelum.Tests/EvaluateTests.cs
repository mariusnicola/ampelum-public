using System.Threading;
using System.Threading.Tasks;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Metrics;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

public sealed class EvaluateTests
{
    sealed record QA : ISignature
    {
        [Input] public string Question { get; init; } = "";
        [Output] public string Answer { get; init; } = "";
    }

    static Prediction<QA> Pred(string answer) => new(
        new QA { Answer = answer },
        new TraceRecord(Guid.NewGuid(), "Predict", typeof(QA),
            new Dictionary<string, object?>(), new Dictionary<string, object?>(),
            Array.Empty<ChatMessage>(), "", null, TimeSpan.Zero, null,
            new Dictionary<string, object?>()));

    [Fact]
    public async Task Run_AllPerfectMatches_Mean_One()
    {
        Func<QA, CancellationToken, Task<Prediction<QA>>> program = (_, _) => Task.FromResult(Pred("x"));
        var eval = new Evaluate<QA>(program, new ExactMatch<QA>("Answer"), maxDegreeOfParallelism: 2);
        var r = await eval.RunAsync(new[]
        {
            new LabeledExample<QA>(new QA(), new QA { Answer = "x" }),
            new LabeledExample<QA>(new QA(), new QA { Answer = "x" }),
        }, TestContext.Current.CancellationToken);
        r.Mean.ShouldBe(1.0);
        r.FailureCount.ShouldBe(0);
    }

    [Fact]
    public async Task Run_SingleExample_StdDev_Zero()
    {
        Func<QA, CancellationToken, Task<Prediction<QA>>> program = (_, _) => Task.FromResult(Pred("x"));
        var r = await new Evaluate<QA>(program, new ExactMatch<QA>("Answer"))
            .RunAsync(new[] { new LabeledExample<QA>(new QA(), new QA { Answer = "x" }) }, TestContext.Current.CancellationToken);
        r.StdDev.ShouldBe(0.0);
    }

    [Fact]
    public async Task Run_ProgramThrows_CollectsFailure_Continues()
    {
        int i = 0;
        Func<QA, CancellationToken, Task<Prediction<QA>>> program = (_, _) =>
        {
            if (Interlocked.Increment(ref i) == 1) throw new InvalidOperationException("boom");
            return Task.FromResult(Pred("x"));
        };
        var r = await new Evaluate<QA>(program, new ExactMatch<QA>("Answer"), maxDegreeOfParallelism: 1)
            .RunAsync(new[]
            {
                new LabeledExample<QA>(new QA(), new QA { Answer = "x" }),
                new LabeledExample<QA>(new QA(), new QA { Answer = "x" }),
            }, TestContext.Current.CancellationToken);
        r.FailureCount.ShouldBe(1);
        r.Mean.ShouldBe(0.5);
        r.Failures[0].Error.ShouldBeOfType<InvalidOperationException>();
        r.Failures[0].Prediction.ShouldBeNull();
    }

    [Fact]
    public async Task Run_MetricThrows_Rethrows()
    {
        Func<QA, CancellationToken, Task<Prediction<QA>>> program = (_, _) => Task.FromResult(Pred("x"));
        var metric = new UserDefined<QA>((_, _) => throw new InvalidOperationException("metric"));
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await new Evaluate<QA>(program, metric).RunAsync(
                new[] { new LabeledExample<QA>(new QA(), new QA()) },
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Run_MetricOutOfRange_Throws()
    {
        Func<QA, CancellationToken, Task<Prediction<QA>>> program = (_, _) => Task.FromResult(Pred("x"));
        var metric = new UserDefined<QA>((_, _) => 2.0);
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await new Evaluate<QA>(program, metric).RunAsync(
                new[] { new LabeledExample<QA>(new QA(), new QA()) },
                TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("[0.0, 1.0]");
    }

    [Fact]
    public async Task Run_Cancellation_Propagates()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        Func<QA, CancellationToken, Task<Prediction<QA>>> program =
            (_, t) => { t.ThrowIfCancellationRequested(); return Task.FromResult(Pred("x")); };
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await new Evaluate<QA>(program, new ExactMatch<QA>("Answer"))
                .RunAsync(new[] { new LabeledExample<QA>(new QA(), new QA()) }, cts.Token));
    }

    [Fact]
    public async Task Run_Progress_InvokedOncePerExample()
    {
        int calls = 0;
        var progress = new Progress<EvaluationProgress>(_ => Interlocked.Increment(ref calls));
        Func<QA, CancellationToken, Task<Prediction<QA>>> program = (_, _) => Task.FromResult(Pred("x"));
        await new Evaluate<QA>(program, new ExactMatch<QA>("Answer"), progress: progress).RunAsync(new[]
        {
            new LabeledExample<QA>(new QA(), new QA { Answer = "x" }),
            new LabeledExample<QA>(new QA(), new QA { Answer = "x" }),
            new LabeledExample<QA>(new QA(), new QA { Answer = "x" }),
        }, TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        calls.ShouldBe(3);
    }

    [Fact]
    public async Task Run_FailureSamples_AreLowestScorers()
    {
        int i = 0;
        Func<QA, CancellationToken, Task<Prediction<QA>>> program = (_, _) =>
            Task.FromResult(Pred(Interlocked.Increment(ref i) % 2 == 0 ? "x" : "y"));
        var r = await new Evaluate<QA>(program, new ExactMatch<QA>("Answer"),
                maxDegreeOfParallelism: 1, failureSampleCount: 2)
            .RunAsync(new[]
            {
                new LabeledExample<QA>(new QA(), new QA { Answer = "x" }),
                new LabeledExample<QA>(new QA(), new QA { Answer = "x" }),
                new LabeledExample<QA>(new QA(), new QA { Answer = "x" }),
                new LabeledExample<QA>(new QA(), new QA { Answer = "x" }),
            }, TestContext.Current.CancellationToken);
        r.FailureSamples.Count.ShouldBeLessThanOrEqualTo(2);
        r.FailureSamples.ShouldAllBe(s => s.Score == 0.0);
    }

    [Fact]
    public async Task Run_EmptyExamples_ReturnsZeroMean()
    {
        Func<QA, CancellationToken, Task<Prediction<QA>>> program = (_, _) => Task.FromResult(Pred("x"));
        var r = await new Evaluate<QA>(program, new ExactMatch<QA>("Answer"))
            .RunAsync(Array.Empty<LabeledExample<QA>>(), TestContext.Current.CancellationToken);
        r.Mean.ShouldBe(0.0);
        r.StdDev.ShouldBe(0.0);
    }

    [Fact]
    public async Task Run_MetricNegativeScore_Throws()
    {
        Func<QA, CancellationToken, Task<Prediction<QA>>> program = (_, _) => Task.FromResult(Pred("x"));
        var metric = new UserDefined<QA>((_, _) => -0.5);
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await new Evaluate<QA>(program, metric).RunAsync(
                new[] { new LabeledExample<QA>(new QA(), new QA()) },
                TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("[0.0, 1.0]");
    }

    [Fact]
    public async Task Run_ExampleScore_ExposesExample()
    {
        var labeled = new LabeledExample<QA>(new QA { Question = "q" }, new QA { Answer = "x" });
        Func<QA, CancellationToken, Task<Prediction<QA>>> program = (_, _) => Task.FromResult(Pred("x"));
        var r = await new Evaluate<QA>(program, new ExactMatch<QA>("Answer"))
            .RunAsync(new[] { labeled }, TestContext.Current.CancellationToken);
        r.Scores[0].Example.ShouldBe(labeled);
    }
}

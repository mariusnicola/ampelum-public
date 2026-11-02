using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Evaluation;
using SignalGrid.Ampelum.Metrics;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

public sealed class EvaluateDatasetTests
{
    sealed record QA : ISignature
    {
        [Input] public string Question { get; init; } = "";
        [Output] public string Answer { get; init; } = "";
    }

    sealed record Numeric : ISignature
    {
        [Input] public string Question { get; init; } = "";
        [Output] public int Value { get; init; }
    }

    sealed class InMemoryDataset(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) : IDataset
    {
        readonly IReadOnlyList<IReadOnlyDictionary<string, object?>> _rows = rows;
        public long? RowCount => _rows.Count;
        public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> EnumerateAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var r in _rows) { ct.ThrowIfCancellationRequested(); yield return r; await Task.Yield(); }
        }
    }

    static TraceRecord Empty() => new(Guid.NewGuid(), "Test", typeof(QA),
        new Dictionary<string, object?>(), new Dictionary<string, object?>(),
        Array.Empty<ChatMessage>(), "", null, TimeSpan.Zero, null, new Dictionary<string, object?>());

    static Func<QA, CancellationToken, Task<Prediction<QA>>> Echo() =>
        (q, _) => Task.FromResult(new Prediction<QA>(new QA { Question = q.Question, Answer = q.Answer }, Empty()));

    [Fact]
    public async Task RunAsync_IDataset_Maps_Rows_To_LabeledExamples()
    {
        var ds = new InMemoryDataset(new[]
        {
            (IReadOnlyDictionary<string, object?>) new Dictionary<string, object?>{["Question"]="q1",["Answer"]="a1"},
            new Dictionary<string, object?>{["Question"]="q2",["Answer"]="a2"},
        });
        var ev = new Evaluate<QA>(Echo(), new ExactMatch<QA>("Answer"));
        var r = await ev.RunAsync(ds, TestContext.Current.CancellationToken);
        r.Scores.Count.ShouldBe(2);
        // Echo returns input Answer (empty) but expected is "a1"/"a2" -> exact match 0
        r.Mean.ShouldBe(0.0);
    }

    [Fact]
    public async Task RunAsync_IDataset_Empty_Returns_Zero_Mean()
    {
        var ds = new InMemoryDataset(Array.Empty<IReadOnlyDictionary<string, object?>>());
        var ev = new Evaluate<QA>(Echo(), new ExactMatch<QA>("Answer"));
        var r = await ev.RunAsync(ds, TestContext.Current.CancellationToken);
        r.Scores.Count.ShouldBe(0);
        r.Mean.ShouldBe(0.0);
    }

    [Fact]
    public async Task RunAsync_IDataset_Honors_Cancellation()
    {
        var ds = new InMemoryDataset(new[]
        {
            (IReadOnlyDictionary<string, object?>) new Dictionary<string, object?>{["Question"]="q1",["Answer"]="a1"},
        });
        var ev = new Evaluate<QA>(Echo(), new ExactMatch<QA>("Answer"));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => ev.RunAsync(ds, cts.Token));
    }

    [Fact]
    public async Task RunAsync_IDataset_NullDataset_Throws()
    {
        var ev = new Evaluate<QA>(Echo(), new ExactMatch<QA>("Answer"));
        await Should.ThrowAsync<ArgumentNullException>(() => ev.RunAsync((IDataset)null!));
    }

    [Fact]
    public async Task RunAsync_IDataset_Missing_Input_Field_Leaves_Default()
    {
        var ds = new InMemoryDataset(new[]
        {
            (IReadOnlyDictionary<string, object?>) new Dictionary<string, object?>{["Answer"]="a"},
        });
        var ev = new Evaluate<QA>(Echo(), new ExactMatch<QA>("Answer"));
        var r = await ev.RunAsync(ds, TestContext.Current.CancellationToken);
        // Question is empty default; pipeline still runs
        r.Scores.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RunAsync_IDataset_Numeric_Conversion()
    {
        var ds = new InMemoryDataset(new[]
        {
            (IReadOnlyDictionary<string, object?>) new Dictionary<string, object?>{["Question"]="q",["Value"]=42L},
        });
        Func<Numeric, CancellationToken, Task<Prediction<Numeric>>> echo =
            (q, _) => Task.FromResult(new Prediction<Numeric>(new Numeric { Question = q.Question, Value = 42 },
                new TraceRecord(Guid.NewGuid(), "Test", typeof(Numeric),
                    new Dictionary<string, object?>(), new Dictionary<string, object?>(),
                    Array.Empty<ChatMessage>(), "", null, TimeSpan.Zero, null, new Dictionary<string, object?>())));
        var ev = new Evaluate<Numeric>(echo, new ExactMatch<Numeric>("Value"));
        var r = await ev.RunAsync(ds, TestContext.Current.CancellationToken);
        r.Scores.Count.ShouldBe(1);
        r.Mean.ShouldBe(1.0);
    }

    [Fact]
    public async Task RunAsync_IDataset_Null_Value_Skips_Conversion()
    {
        var ds = new InMemoryDataset(new[]
        {
            (IReadOnlyDictionary<string, object?>) new Dictionary<string, object?>{["Question"]="q",["Answer"]=null},
        });
        var ev = new Evaluate<QA>(Echo(), new ExactMatch<QA>("Answer"));
        var r = await ev.RunAsync(ds, TestContext.Current.CancellationToken);
        r.Scores.Count.ShouldBe(1);
    }
}

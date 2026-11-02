using System.Runtime.CompilerServices;
using SignalGrid.Ampelum.Evaluation;
using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Retrieval.Evaluation;
using Shouldly;
using Xunit;

public sealed class RetrievalEvalHarnessTests
{
    sealed class FakeRetriever : IRetriever
    {
        readonly Dictionary<string, string[]> _map;
        public FakeRetriever(Dictionary<string, string[]> m) => _map = m;
        public Task<IReadOnlyList<RetrievedDocument>> RetrieveAsync(
            string query, int topK, IReadOnlyDictionary<string, object?>? filters = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RetrievedDocument>>(
                (_map.TryGetValue(query, out var ids) ? ids : Array.Empty<string>())
                .Take(topK)
                .Select((id, i) => new RetrievedDocument(id, "", 1.0 - i * 0.1, new Dictionary<string, object?>()))
                .ToList());
    }

    sealed class InMemoryDataset(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) : IDataset
    {
        public long? RowCount => rows.Count;
        public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> EnumerateAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var r in rows) { ct.ThrowIfCancellationRequested(); yield return r; await Task.Yield(); }
        }
    }

    [Fact]
    public async Task Empty_Dataset_Returns_Zero_Examples()
    {
        var h = new RetrievalEvalHarness(
            new FakeRetriever(new Dictionary<string, string[]>()),
            new IRetrievalMetric[] { new RecallAtK(5) });
        var ds = new InMemoryDataset(Array.Empty<IReadOnlyDictionary<string, object?>>());
        var r = await h.RunAsync(ds, TestContext.Current.CancellationToken);
        r.Examples.ShouldBe(0);
        r.Means["recall@5"].ShouldBe(0.0);
    }

    [Fact]
    public async Task Computes_Mean_Across_Rows()
    {
        var h = new RetrievalEvalHarness(
            new FakeRetriever(new() { ["q1"] = new[] { "a", "b" }, ["q2"] = new[] { "x" } }),
            new IRetrievalMetric[] { new RecallAtK(5) });
        var ds = new InMemoryDataset(new[]
        {
            (IReadOnlyDictionary<string, object?>) new Dictionary<string, object?> { ["query"] = "q1", ["gold_ids"] = new[] { "a", "b" } },
            new Dictionary<string, object?> { ["query"] = "q2", ["gold_ids"] = new[] { "a" } },
        });
        var r = await h.RunAsync(ds, TestContext.Current.CancellationToken);
        r.Examples.ShouldBe(2);
        r.Means["recall@5"].ShouldBe(0.5, 1e-9);
    }

    [Fact]
    public void ParseIds_From_JsonArrayString() =>
        RetrievalEvalHarness.ParseIds("[\"a\",\"b\"]").ShouldBe(new[] { "a", "b" });

    [Fact]
    public void ParseIds_From_CsvString() =>
        RetrievalEvalHarness.ParseIds("a, b ,c").ShouldBe(new[] { "a", "b", "c" });

    [Fact]
    public void ParseIds_From_IEnumerable() =>
        RetrievalEvalHarness.ParseIds(new object?[] { "a", "b" }).ShouldBe(new[] { "a", "b" });

    [Fact]
    public void ParseIds_Null_Empty()
    {
        RetrievalEvalHarness.ParseIds(null).ShouldBeEmpty();
        RetrievalEvalHarness.ParseIds(42).ShouldBe(new[] { "42" });
    }

    [Fact]
    public void Ctor_Null_Args_Throw()
    {
        Should.Throw<ArgumentNullException>(() => new RetrievalEvalHarness(null!, new IRetrievalMetric[] { new MRR() }));
        Should.Throw<ArgumentNullException>(() => new RetrievalEvalHarness(new FakeRetriever(new()), null!));
    }

    [Fact]
    public void Ctor_Empty_Metrics_Throws() =>
        Should.Throw<ArgumentException>(() => new RetrievalEvalHarness(new FakeRetriever(new()), Array.Empty<IRetrievalMetric>()));

    [Fact]
    public void Ctor_NonPositive_TopK_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new RetrievalEvalHarness(new FakeRetriever(new()), new IRetrievalMetric[] { new MRR() }, topK: 0));

    [Fact]
    public async Task Honors_Cancellation()
    {
        var h = new RetrievalEvalHarness(
            new FakeRetriever(new()),
            new IRetrievalMetric[] { new MRR() });
        var ds = new InMemoryDataset(new[]
        {
            (IReadOnlyDictionary<string, object?>) new Dictionary<string, object?> { ["query"] = "q", ["gold_ids"] = new[] { "a" } },
        });
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => h.RunAsync(ds, cts.Token));
    }

    [Fact]
    public async Task Multiple_Metrics_Each_Have_Mean_In_Result()
    {
        var h = new RetrievalEvalHarness(
            new FakeRetriever(new() { ["q"] = new[] { "a" } }),
            new IRetrievalMetric[] { new RecallAtK(5), new MRR(), new PrecisionAtK(5), new NDCGAtK(5) });
        var ds = new InMemoryDataset(new[]
        {
            (IReadOnlyDictionary<string, object?>) new Dictionary<string, object?> { ["query"] = "q", ["gold_ids"] = new[] { "a" } },
        });
        var r = await h.RunAsync(ds, TestContext.Current.CancellationToken);
        r.Means.Count.ShouldBe(4);
        r.Means["recall@5"].ShouldBe(1.0);
        r.Means["mrr"].ShouldBe(1.0);
    }

    [Fact]
    public async Task Missing_Query_And_Gold_Defaults_Empty()
    {
        var h = new RetrievalEvalHarness(
            new FakeRetriever(new()),
            new IRetrievalMetric[] { new RecallAtK(5) });
        var ds = new InMemoryDataset(new[]
        {
            (IReadOnlyDictionary<string, object?>) new Dictionary<string, object?>(),
        });
        var r = await h.RunAsync(ds, TestContext.Current.CancellationToken);
        r.Examples.ShouldBe(1);
    }

    [Fact]
    public async Task NullDataset_Throws()
    {
        var h = new RetrievalEvalHarness(new FakeRetriever(new()), new IRetrievalMetric[] { new MRR() });
        await Should.ThrowAsync<ArgumentNullException>(() => h.RunAsync(null!));
    }
}

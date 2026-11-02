using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.VectorStores.Pinecone;
using Shouldly;
using Xunit;

namespace Ampelum.VectorStores.Pinecone.Tests;

public sealed class PineconeVectorStoreTests
{
    private sealed class FakeIndex : IPineconeIndex
    {
        public List<(string Id, ReadOnlyMemory<float> Vector, IReadOnlyDictionary<string, string> Metadata)> Stored { get; } = new();
        public string? LastNamespace;
        public IReadOnlyDictionary<string, string>? LastFilter;
        public bool Disposed;

        public Task UpsertAsync(string @namespace, IReadOnlyList<(string Id, ReadOnlyMemory<float> Vector, IReadOnlyDictionary<string, string> Metadata)> points, CancellationToken ct)
        {
            LastNamespace = @namespace;
            foreach (var p in points) Stored.Add(p);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<(string Id, double Score, IReadOnlyDictionary<string, string> Metadata)>> QueryAsync(string @namespace, ReadOnlyMemory<float> queryVector, int topK, IReadOnlyDictionary<string, string>? filterEquals, CancellationToken ct)
        {
            LastNamespace = @namespace;
            LastFilter = filterEquals;
            var hits = Stored.Take(topK).Select((p, i) => (p.Id, (double)(1.0 - i * 0.1), p.Metadata)).ToList();
            return Task.FromResult<IReadOnlyList<(string, double, IReadOnlyDictionary<string, string>)>>(hits);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task UpsertQuery_RoundTrip()
    {
        var idx = new FakeIndex();
        await using var sut = new PineconeVectorStore(
            new PineconeOptions { ApiKey = "k", Environment = "e", IndexName = "i", Namespace = "ns" }, idx);
        await sut.UpsertAsync(
            [new RetrievedDocument("a", "alpha", 0, new Dictionary<string, object?> { ["k"] = "v" })],
            [new ReadOnlyMemory<float>([1f, 0f, 0f])],
            TestContext.Current.CancellationToken);
        var r = await sut.QueryAsync(new ReadOnlyMemory<float>([1f, 0f, 0f]), 1, ct: TestContext.Current.CancellationToken);
        r.Single().Document.Id.ShouldBe("a");
        r.Single().Document.Text.ShouldBe("alpha");
        r.Single().Document.Metadata["k"].ShouldBe("v");
        idx.LastNamespace.ShouldBe("ns");
    }

    [Fact]
    public async Task Upsert_LengthMismatch_Throws()
    {
        await using var sut = new PineconeVectorStore(
            new PineconeOptions { ApiKey = "k", Environment = "e", IndexName = "i" }, new FakeIndex());
        await Should.ThrowAsync<ArgumentException>(() => sut.UpsertAsync(
            [new RetrievedDocument("a", "t", 0, new Dictionary<string, object?>())],
            new ReadOnlyMemory<float>[2],
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Query_PassesFiltersToBackend()
    {
        var idx = new FakeIndex();
        await using var sut = new PineconeVectorStore(
            new PineconeOptions { ApiKey = "k", Environment = "e", IndexName = "i" }, idx);
        await sut.UpsertAsync(
            [new RetrievedDocument("a", "x", 0, new Dictionary<string, object?>())],
            [new ReadOnlyMemory<float>([1f, 0f, 0f])],
            TestContext.Current.CancellationToken);
        await sut.QueryAsync(new ReadOnlyMemory<float>([1f, 0f, 0f]), 1,
            filters: new Dictionary<string, object?> { ["lang"] = "en" },
            ct: TestContext.Current.CancellationToken);
        idx.LastFilter!["lang"].ShouldBe("en");
    }

    [Fact]
    public async Task DisposeAsync_DisposesIndex()
    {
        var idx = new FakeIndex();
        var sut = new PineconeVectorStore(new PineconeOptions { ApiKey = "k", Environment = "e", IndexName = "i" }, idx);
        await sut.DisposeAsync();
        idx.Disposed.ShouldBeTrue();
    }

    [Fact]
    public void Options_Defaults()
    {
        var o = new PineconeOptions { ApiKey = "k", Environment = "e", IndexName = "i" };
        o.Namespace.ShouldBe("");
    }
}

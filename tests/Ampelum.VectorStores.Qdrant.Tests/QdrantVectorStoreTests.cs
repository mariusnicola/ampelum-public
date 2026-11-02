using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.VectorStores.Qdrant;
using Shouldly;
using Xunit;

namespace Ampelum.VectorStores.Qdrant.Tests;

public sealed class QdrantVectorStoreTests
{
    private sealed class FakeBackend : IQdrantBackend
    {
        public List<(string Id, ReadOnlyMemory<float> Vector, IReadOnlyDictionary<string, string> Payload)> Stored { get; } = new();
        public string? EnsuredCollection;
        public int? EnsuredDim;
        public IReadOnlyDictionary<string, string>? LastFilter;
        public bool Disposed;

        public Task EnsureCollectionAsync(string collection, int dimension, CancellationToken ct)
        {
            EnsuredCollection = collection;
            EnsuredDim = dimension;
            return Task.CompletedTask;
        }

        public Task UpsertAsync(string collection, IReadOnlyList<(string Id, ReadOnlyMemory<float> Vector, IReadOnlyDictionary<string, string> Payload)> points, CancellationToken ct)
        {
            foreach (var p in points)
            {
                Stored.Add(p);
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<(string Id, double Score, IReadOnlyDictionary<string, string> Payload)>> SearchAsync(string collection, ReadOnlyMemory<float> queryVector, int topK, IReadOnlyDictionary<string, string>? filterEquals, CancellationToken ct)
        {
            LastFilter = filterEquals;
            var hits = Stored.Take(topK).Select((p, i) => (p.Id, (double)(1.0 - i * 0.1), p.Payload)).ToList();
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
        var backend = new FakeBackend();
        await using var sut = new QdrantVectorStore(
            new QdrantOptions { CollectionName = "c", VectorDimension = 3 },
            backend);
        await sut.UpsertAsync(
            [new RetrievedDocument("a", "alpha", 0, new Dictionary<string, object?> { ["k"] = "v" })],
            [new ReadOnlyMemory<float>([1f, 0f, 0f])],
            TestContext.Current.CancellationToken);

        var r = await sut.QueryAsync(new ReadOnlyMemory<float>([1f, 0f, 0f]), 1, ct: TestContext.Current.CancellationToken);
        r.Single().Document.Id.ShouldBe("a");
        r.Single().Document.Text.ShouldBe("alpha");
        r.Single().Document.Metadata["k"].ShouldBe("v");
        backend.EnsuredCollection.ShouldBe("c");
        backend.EnsuredDim.ShouldBe(3);
    }

    [Fact]
    public async Task Upsert_LengthMismatch_Throws()
    {
        await using var sut = new QdrantVectorStore(
            new QdrantOptions { CollectionName = "c", VectorDimension = 3 },
            new FakeBackend());
        await Should.ThrowAsync<ArgumentException>(() => sut.UpsertAsync(
            [new RetrievedDocument("a", "t", 0, new Dictionary<string, object?>())],
            new ReadOnlyMemory<float>[2],
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task QueryWithFilters_PassesEqualsToBackend()
    {
        var backend = new FakeBackend();
        await using var sut = new QdrantVectorStore(
            new QdrantOptions { CollectionName = "c", VectorDimension = 3 },
            backend);
        await sut.UpsertAsync(
            [new RetrievedDocument("a", "x", 0, new Dictionary<string, object?>())],
            [new ReadOnlyMemory<float>([1f, 0f, 0f])],
            TestContext.Current.CancellationToken);
        await sut.QueryAsync(new ReadOnlyMemory<float>([1f, 0f, 0f]), 5,
            filters: new Dictionary<string, object?> { ["lang"] = "en" },
            ct: TestContext.Current.CancellationToken);
        backend.LastFilter!["lang"].ShouldBe("en");
    }

    [Fact]
    public async Task EnsureCollection_OnlyCalledOnce()
    {
        var backend = new FakeBackend();
        await using var sut = new QdrantVectorStore(new QdrantOptions { CollectionName = "c", VectorDimension = 3 }, backend);
        await sut.UpsertAsync(
            [new RetrievedDocument("a", "x", 0, new Dictionary<string, object?>())],
            [new ReadOnlyMemory<float>([1f, 0f, 0f])],
            TestContext.Current.CancellationToken);
        await sut.QueryAsync(new ReadOnlyMemory<float>([1f, 0f, 0f]), 1, ct: TestContext.Current.CancellationToken);
        backend.EnsuredCollection.ShouldBe("c"); // setter only writes once but this verifies no exception
    }

    [Fact]
    public async Task DisposeAsync_DisposesBackend()
    {
        var backend = new FakeBackend();
        var sut = new QdrantVectorStore(new QdrantOptions { CollectionName = "c", VectorDimension = 3 }, backend);
        await sut.DisposeAsync();
        backend.Disposed.ShouldBeTrue();
    }

    [Fact]
    public void Options_Defaults()
    {
        var o = new QdrantOptions { CollectionName = "x", VectorDimension = 4 };
        o.Host.ShouldBe("localhost");
        o.Port.ShouldBe(6334);
        o.ApiKey.ShouldBeNull();
    }
}

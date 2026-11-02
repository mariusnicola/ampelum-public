using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Retrieval.Persistence;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Retrieval;

public sealed class VectorIndexSerializerTests
{
    [Fact]
    public async Task RoundTrip_InMemoryStore_PreservesData()
    {
        var store = new InMemoryVectorStore();
        var docs = new[] { new RetrievedDocument("a", "alpha", 0, new Dictionary<string, object?> { ["k"] = "v" }) };
        var vectors = new[] { new ReadOnlyMemory<float>([0.1f, 0.2f, 0.3f]) };
        await store.UpsertAsync(docs, vectors, TestContext.Current.CancellationToken);

        using var ms = new MemoryStream();
        await VectorIndexSerializer.SaveAsync(store, ms, TestContext.Current.CancellationToken);
        ms.Position = 0;
        var restored = new InMemoryVectorStore();
        await VectorIndexSerializer.LoadAsync(restored, ms, TestContext.Current.CancellationToken);

        var qr = await restored.QueryAsync(new ReadOnlyMemory<float>([0.1f, 0.2f, 0.3f]), 1, ct: TestContext.Current.CancellationToken);
        qr.Single().Document.Id.ShouldBe("a");
        qr.Single().Document.Metadata["k"]!.ToString().ShouldBe("v");
    }

    [Fact]
    public async Task SaveLoad_TwoRounds_ProducesIdenticalBytes()
    {
        var store = new InMemoryVectorStore();
        await store.UpsertAsync(
            [new RetrievedDocument("a", "x", 0, new Dictionary<string, object?>())],
            [new ReadOnlyMemory<float>([1f, 2f])],
            TestContext.Current.CancellationToken);
        using var first = new MemoryStream();
        await VectorIndexSerializer.SaveAsync(store, first, TestContext.Current.CancellationToken);
        var bytes = first.ToArray();

        var restored = new InMemoryVectorStore();
        using var seek = new MemoryStream(bytes);
        await VectorIndexSerializer.LoadAsync(restored, seek, TestContext.Current.CancellationToken);
        using var second = new MemoryStream();
        await VectorIndexSerializer.SaveAsync(restored, second, TestContext.Current.CancellationToken);
        second.ToArray().ShouldBe(bytes);
    }

    [Fact]
    public async Task Save_NonInMemoryStore_Throws()
    {
        var fake = new FakeStore();
        using var ms = new MemoryStream();
        await Should.ThrowAsync<NotSupportedException>(() => VectorIndexSerializer.SaveAsync(fake, ms, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Load_NonInMemoryStore_Throws()
    {
        var fake = new FakeStore();
        using var ms = new MemoryStream();
        await Should.ThrowAsync<NotSupportedException>(() => VectorIndexSerializer.LoadAsync(fake, ms, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Load_BadMagic_Throws()
    {
        using var ms = new MemoryStream(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 });
        var store = new InMemoryVectorStore();
        await Should.ThrowAsync<InvalidDataException>(() => VectorIndexSerializer.LoadAsync(store, ms, TestContext.Current.CancellationToken));
    }

    private sealed class FakeStore : IVectorStore
    {
        public Task UpsertAsync(IReadOnlyList<RetrievedDocument> documents, ReadOnlyMemory<float>[] vectors, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<(RetrievedDocument Document, double Score)>> QueryAsync(ReadOnlyMemory<float> queryVector, int topK, IReadOnlyDictionary<string, object?>? filters = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(RetrievedDocument, double)>>([]);
    }
}

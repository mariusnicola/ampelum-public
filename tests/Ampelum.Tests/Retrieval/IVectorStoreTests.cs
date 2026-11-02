using SignalGrid.Ampelum.Retrieval;
using Shouldly;
using Xunit;

public sealed class IVectorStoreTests
{
    sealed class StubVectorStore : IVectorStore
    {
        public Task UpsertAsync(
            IReadOnlyList<RetrievedDocument> documents,
            ReadOnlyMemory<float>[] vectors,
            CancellationToken ct = default)
        {
            if (documents.Count != vectors.Length)
                throw new ArgumentException("documents.Count must equal vectors.Length.");
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<(RetrievedDocument Document, double Score)>> QueryAsync(
            ReadOnlyMemory<float> queryVector,
            int topK,
            IReadOnlyDictionary<string, object?>? filters = null,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(RetrievedDocument, double)>>([]);
    }

    [Fact]
    public async Task UpsertAsync_MismatchedLengths_ThrowsArgumentException()
    {
        var store = new StubVectorStore();
        var docs = new[] { new RetrievedDocument("d1", "t", 0, new Dictionary<string, object?>()) };
        var vectors = Array.Empty<ReadOnlyMemory<float>>();
        await Should.ThrowAsync<ArgumentException>(() =>
            store.UpsertAsync(docs, vectors, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpsertAsync_MatchingLengths_Succeeds()
    {
        var store = new StubVectorStore();
        var docs = new[] { new RetrievedDocument("d1", "t", 0, new Dictionary<string, object?>()) };
        var vectors = new[] { new ReadOnlyMemory<float>([1f, 0f]) };
        await store.UpsertAsync(docs, vectors, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task QueryAsync_StubReturnsEmpty()
    {
        var store = new StubVectorStore();
        var result = await store.QueryAsync(new ReadOnlyMemory<float>([1f, 0f]), 5, ct: TestContext.Current.CancellationToken);
        result.ShouldBeEmpty();
    }
}

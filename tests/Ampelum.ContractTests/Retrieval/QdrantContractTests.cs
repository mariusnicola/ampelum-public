using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.VectorStores.Qdrant;
using Shouldly;
using Xunit;

namespace Ampelum.ContractTests.Retrieval;

[Trait("Category", "Integration")]
public sealed class QdrantContractTests
{
    private static string? Host => Environment.GetEnvironmentVariable("AMPELUM_INTEGRATION_QDRANT_HOST");

    [Fact]
    public async Task RoundTrip_AgainstRealQdrant()
    {
        if (Host is null)
        {
            return; // env-gated; CI without env var skips silently
        }
        await using var sut = new QdrantVectorStore(new QdrantOptions
        {
            CollectionName = $"ampelum_ct_{Guid.NewGuid():N}",
            VectorDimension = 3,
            Host = Host,
        });
        await sut.UpsertAsync(
            [new RetrievedDocument(Guid.NewGuid().ToString(), "x", 0, new Dictionary<string, object?>())],
            [new ReadOnlyMemory<float>([1, 0, 0])],
            TestContext.Current.CancellationToken);
        var r = await sut.QueryAsync(new ReadOnlyMemory<float>([1, 0, 0]), 1, ct: TestContext.Current.CancellationToken);
        r.Count.ShouldBe(1);
    }
}

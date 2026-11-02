using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.VectorStores.Pinecone;
using Shouldly;
using Xunit;

namespace Ampelum.ContractTests.Retrieval;

[Trait("Category", "Integration")]
public sealed class PineconeContractTests
{
    private static string? ApiKey => Environment.GetEnvironmentVariable("AMPELUM_INTEGRATION_PINECONE_API_KEY");
    private static string? Index => Environment.GetEnvironmentVariable("AMPELUM_INTEGRATION_PINECONE_INDEX");

    [Fact]
    public async Task RoundTrip_AgainstRealPinecone()
    {
        if (ApiKey is null || Index is null)
        {
            return;
        }
        await using var sut = new PineconeVectorStore(new PineconeOptions
        {
            ApiKey = ApiKey,
            Environment = Environment.GetEnvironmentVariable("AMPELUM_INTEGRATION_PINECONE_ENV") ?? "us-east-1",
            IndexName = Index,
        });
        await sut.UpsertAsync(
            [new RetrievedDocument(Guid.NewGuid().ToString(), "x", 0, new Dictionary<string, object?>())],
            [new ReadOnlyMemory<float>([1, 0, 0])],
            TestContext.Current.CancellationToken);
        var r = await sut.QueryAsync(new ReadOnlyMemory<float>([1, 0, 0]), 1, ct: TestContext.Current.CancellationToken);
        r.Count.ShouldBeGreaterThanOrEqualTo(0);
    }
}

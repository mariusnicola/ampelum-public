using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.VectorStores.PgVector;
using Shouldly;
using Xunit;

namespace Ampelum.ContractTests.Retrieval;

[Trait("Category", "Docker")]
[Trait("Category", "Integration")]
public sealed class PgVectorContractTests
{
    private static string? Conn => Environment.GetEnvironmentVariable("AMPELUM_INTEGRATION_PG_CONN");

    [Fact]
    public async Task RoundTrip_AgainstRealPgVector()
    {
        if (Conn is null)
        {
            return;
        }
        await using var sut = new PgVectorStore(new PgVectorOptions
        {
            ConnectionString = Conn,
            VectorDimension = 3,
            TableName = $"ampelum_ct_{Guid.NewGuid():N}",
        });
        await sut.UpsertAsync(
            [new RetrievedDocument("a", "x", 0, new Dictionary<string, object?>())],
            [new ReadOnlyMemory<float>([1, 0, 0])],
            TestContext.Current.CancellationToken);
        var r = await sut.QueryAsync(new ReadOnlyMemory<float>([1, 0, 0]), 1, ct: TestContext.Current.CancellationToken);
        r.Single().Document.Id.ShouldBe("a");
    }
}

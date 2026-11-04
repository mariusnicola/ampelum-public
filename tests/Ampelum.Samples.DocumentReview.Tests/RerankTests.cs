using Ampelum.Samples.DocumentReview;
using Shouldly;
using Xunit;

namespace Ampelum.Samples.DocumentReview.Tests;

public sealed class RerankTests
{
    [Theory]
    [InlineData("Cohere")]
    [InlineData("Voyage")]
    public async Task Rerank_SendsEveryPageAndKeepsTheVendorOrder(string vendor)
    {
        var kind = Enum.Parse<RerankerKind>(vendor);
        await using var rig = Rig.Create();
        var files = await rig.ReadableAsync(TestContext.Current.CancellationToken);

        var ranked = await rig.Desk.RerankAsync(kind, FakeData.RerankerKey, FakeData.RerankerModel, files, SupplierDocuments.Question, TestContext.Current.CancellationToken);

        var call = rig.Rerank.Calls.ShouldHaveSingleItem();
        call.Kind.ShouldBe(kind);
        call.Authorization.ShouldBe($"Bearer {FakeData.RerankerKey}");
        call.Model.ShouldBe(FakeData.RerankerModel);
        call.Query.ShouldBe(SupplierDocuments.Question);
        call.Documents.Count.ShouldBe(files.Sum(f => f.Pages.Count));
        call.Documents.ShouldContain(d => d.Contains("Total liability is capped", StringComparison.Ordinal));
        call.Limit.ShouldBe(ReviewDesk.TopClauses);

        ranked.Select(r => r.Id).ShouldBe(["supply-agreement.pdf#page=1", "subcontract-notice.pdf#page=1"]);
        ranked[0].Score.ShouldBe(1.0, 0.0001);
        ranked[1].Score.ShouldBe(1.0 / 3, 0.0001);
        ranked[1].Text.ShouldContain("Liability for subcontractors");
    }

    [Theory]
    [InlineData("Cohere")]
    [InlineData("Voyage")]
    public async Task Rerank_AServiceNamingAPageThatDoesNotExist_FailsWithAClearError(string vendor)
    {
        await using var rig = Rig.Create();
        rig.Rerank.IndexShift = 100;
        var files = await rig.ReadableAsync(TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<InvalidDataException>(() =>
            rig.Desk.RerankAsync(Enum.Parse<RerankerKind>(vendor), FakeData.RerankerKey, FakeData.RerankerModel, files, SupplierDocuments.Question, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain($"{files.Sum(f => f.Pages.Count)} candidates");
    }

    [Fact]
    public async Task Rerank_WrongKey_Fails()
    {
        await using var rig = Rig.Create();
        var files = await rig.ReadableAsync(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<HttpRequestException>(() =>
            rig.Desk.RerankAsync(RerankerKind.Voyage, "not-the-key", FakeData.RerankerModel, files, SupplierDocuments.Question, TestContext.Current.CancellationToken));
    }
}

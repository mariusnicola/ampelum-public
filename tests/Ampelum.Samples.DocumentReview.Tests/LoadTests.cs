using Ampelum.Samples.DocumentReview;
using Shouldly;
using Xunit;

namespace Ampelum.Samples.DocumentReview.Tests;

public sealed class LoadTests
{
    [Fact]
    public async Task Load_ReadsEveryPageOfEveryPdf()
    {
        await using var rig = Rig.Create();

        var files = await rig.LoadAsync(TestContext.Current.CancellationToken);

        var agreement = files.Single(f => f.Name == SupplierDocuments.SupplyAgreement);
        agreement.Pages.Select(p => p.Id).ShouldBe(["supply-agreement.pdf#page=1", "supply-agreement.pdf#page=2"]);
        agreement.Pages[0].Text.ShouldContain("Total liability is capped at the fees paid in the prior year.");
        agreement.Pages[1].Text.ShouldContain("Delivery is made to the buyer warehouse");
        files.Single(f => f.Name == SupplierDocuments.SubcontractNotice).Pages.ShouldHaveSingleItem()
            .Text.ShouldContain("The supplier may subcontract work.");
    }

    [Fact]
    public async Task Load_FileThatIsNotAPdf_HasNoPagesAndCarriesTheLoadersError()
    {
        await using var rig = Rig.Create();

        var files = await rig.LoadAsync(TestContext.Current.CancellationToken);

        var damaged = files.Single(f => f.Name == SupplierDocuments.Damaged);
        damaged.Pages.ShouldBeEmpty();
        damaged.Error.ShouldNotBeNull().ShouldContain($"'{SupplierDocuments.Damaged}' is not a readable PDF");
        files.Where(f => f.Name != SupplierDocuments.Damaged).ShouldAllBe(f => f.Error == null);
    }

    [Fact]
    public void NoPdfIsCheckedIn()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SignalGrid.Ampelum.sln")))
            root = root.Parent;
        root.ShouldNotBeNull();

        foreach (var project in new[] { "samples/Ampelum.Samples.DocumentReview", "tests/Ampelum.Samples.DocumentReview.Tests" })
        {
            var found = Directory.EnumerateFiles(Path.Combine(root.FullName, project), "*.pdf", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));
            found.ShouldBeEmpty();
        }
    }
}

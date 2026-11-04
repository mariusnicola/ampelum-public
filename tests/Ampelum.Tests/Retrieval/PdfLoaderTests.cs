using SignalGrid.Ampelum.Retrieval.Loaders;
using Ampelum.Tests.Retrieval.Fixtures;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Retrieval;

public sealed class PdfLoaderTests
{
    [Fact]
    public async Task LoadAsync_OnePagePdf_EmitsOneDocument()
    {
        using var ms = new MemoryStream(PdfTestFixtures.Build("Hello"));
        var sut = new PdfLoader();
        var docs = new List<Document>();
        await foreach (var d in sut.LoadAsync(ms, new DocumentLoadContext(), TestContext.Current.CancellationToken))
        {
            docs.Add(d);
        }
        docs.Count.ShouldBe(1);
        docs[0].Text.ShouldContain("Hello");
        docs[0].Metadata["page"].ShouldBe(1);
    }

    [Fact]
    public async Task LoadAsync_TwoPagePdf_EmitsTwoDocuments_WithSequentialIds()
    {
        using var ms = new MemoryStream(PdfTestFixtures.Build("Page1Text", "Page2Text"));
        var sut = new PdfLoader();
        var docs = new List<Document>();
        await foreach (var d in sut.LoadAsync(ms, new DocumentLoadContext { SourceUri = "test.pdf" }, TestContext.Current.CancellationToken))
        {
            docs.Add(d);
        }
        docs.Count.ShouldBe(2);
        docs[0].Id.ShouldBe("test.pdf#page=1");
        docs[1].Id.ShouldBe("test.pdf#page=2");
        docs[0].Metadata["source"].ShouldBe("test.pdf");
    }

    [Fact]
    public async Task LoadAsync_BudgetExceeded_Throws()
    {
        using var ms = new MemoryStream(PdfTestFixtures.Build("X"));
        var sut = new PdfLoader();
        await Should.ThrowAsync<LoaderBudgetExceededException>(async () =>
        {
            await foreach (var _ in sut.LoadAsync(ms, new DocumentLoadContext { MaxDocumentBytes = 1 }, TestContext.Current.CancellationToken))
            {
            }
        });
    }

    static async Task<InvalidDataException> LoadFailure(byte[] bytes, DocumentLoadContext context)
    {
        using var ms = new MemoryStream(bytes);
        return await Should.ThrowAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in new PdfLoader().LoadAsync(ms, context, TestContext.Current.CancellationToken))
            {
            }
        });
    }

    [Fact]
    public async Task LoadAsync_CorruptStream_Throws_AndKeepsTheReasonAsTheInnerException()
    {
        var ex = await LoadFailure(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }, new DocumentLoadContext());

        ex.InnerException.ShouldNotBeNull();
    }

    [Fact]
    public async Task LoadAsync_ANonPdf_Throws_NamingTheSource()
    {
        var ex = await LoadFailure(System.Text.Encoding.UTF8.GetBytes("this is plain text"), new DocumentLoadContext { SourceUri = "notes.txt" });

        ex.Message.ShouldContain("notes.txt");
    }

    [Fact]
    public async Task LoadAsync_AnEmptyStream_Throws()
    {
        await LoadFailure([], new DocumentLoadContext());
    }

    [Fact]
    public void LoaderBudgetExceededException_ExposesProperties()
    {
        var ex = new LoaderBudgetExceededException(10, 20);
        ex.Limit.ShouldBe(10);
        ex.Actual.ShouldBe(20);
        ex.Message.ShouldContain("10");
        ex.Message.ShouldContain("20");
    }

    [Fact]
    public async Task LoadAsync_NoSourceUri_IdContainsPage()
    {
        using var ms = new MemoryStream(PdfTestFixtures.Build("X"));
        var sut = new PdfLoader();
        var docs = new List<Document>();
        await foreach (var d in sut.LoadAsync(ms, new DocumentLoadContext(), TestContext.Current.CancellationToken))
        {
            docs.Add(d);
        }
        docs[0].Id.ShouldBe("page=1");
    }
}

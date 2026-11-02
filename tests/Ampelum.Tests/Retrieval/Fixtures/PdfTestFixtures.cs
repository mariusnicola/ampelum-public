using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Ampelum.Tests.Retrieval.Fixtures;

internal static class PdfTestFixtures
{
    public static byte[] Build(params string[] pageTexts)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pageTexts)
        {
            var page = builder.AddPage(PageSize.A4);
            page.AddText(text, 12, new PdfPoint(50, 750), font);
        }
        return builder.Build();
    }
}

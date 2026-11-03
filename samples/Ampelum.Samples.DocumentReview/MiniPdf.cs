using System.Text;

namespace Ampelum.Samples.DocumentReview;

/// <summary>
/// Writes a plain PDF with one line of text per page. The sample builds its input at run time
/// with this, so no binary file is checked into the repository.
/// </summary>
internal static class MiniPdf
{
    public static byte[] Build(IReadOnlyList<string> pageTexts)
    {
        // Object numbers: 1 catalog, 2 page tree, 3 font, then a page and its content stream per page.
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [" + string.Join(" ", pageTexts.Select((_, i) => $"{4 + 2 * i} 0 R")) + $"] /Count {pageTexts.Count} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        for (var i = 0; i < pageTexts.Count; i++)
        {
            bodies.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + 2 * i} 0 R >>");
            var stream = $"BT /F1 11 Tf 40 780 Td ({Escape(pageTexts[i])}) Tj ET";
            bodies.Add($"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream");
        }

        // Every character is ASCII, so a string offset is a byte offset in the cross-reference table.
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < bodies.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {bodies.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {bodies.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    static string Escape(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);
}

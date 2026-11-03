namespace Ampelum.Samples.DocumentReview;

/// <summary>The incoming supplier documents of the demo. Text is plain ASCII so the PDF writer can emit it directly.</summary>
internal static class SupplierDocuments
{
    public const string SupplyAgreement = "supply-agreement.pdf";
    public const string SubcontractNotice = "subcontract-notice.pdf";
    public const string DeliverySchedule = "delivery-schedule.pdf";
    public const string Addendum = "addendum.pdf";
    public const string Damaged = "damaged-scan.pdf";

    public const string Question = "liability cap and indemnity";

    /// <summary>Documents that go into the overnight summary batch.</summary>
    public static readonly string[] Overnight = [SupplyAgreement, SubcontractNotice, DeliverySchedule];

    /// <summary>The document the supplier withdrew after it was queued.</summary>
    public const string Withdrawn = Addendum;

    static readonly Dictionary<string, string[]> Pages = new()
    {
        [SupplyAgreement] =
        [
            "Clause 7. Total liability is capped at the fees paid in the prior year. Indemnity excludes gross negligence. This liability cap is final.",
            "Clause 9. Delivery is made to the buyer warehouse within thirty days of order.",
        ],
        [SubcontractNotice] =
        [
            "Clause 3. The supplier may subcontract work. Liability for subcontractors follows applicable law.",
        ],
        [DeliverySchedule] =
        [
            "Schedule A. Weekly deliveries every Monday. Payment is due in sixty days.",
        ],
        [Addendum] =
        [
            "Addendum 1. The parties agree to extend the term by twelve months.",
        ],
    };

    /// <summary>Writes every document as a PDF into <paramref name="directory"/>, plus one file that is not a PDF.</summary>
    public static async Task<IReadOnlyList<string>> WriteAllAsync(string directory, CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        foreach (var (name, pages) in Pages)
        {
            var path = Path.Combine(directory, name);
            await File.WriteAllBytesAsync(path, MiniPdf.Build(pages), cancellationToken);
            paths.Add(path);
        }
        var damaged = Path.Combine(directory, Damaged);
        await File.WriteAllTextAsync(damaged, "scanner jam, no pdf data", cancellationToken);
        paths.Add(damaged);
        return paths;
    }
}

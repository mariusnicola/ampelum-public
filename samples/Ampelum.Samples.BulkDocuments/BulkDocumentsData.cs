namespace Ampelum.Samples.BulkDocuments;

/// <summary>Documents the sample summarizes.</summary>
public static class BulkDocumentsData
{
    public static IReadOnlyList<Document> Documents { get; } =
    [
        new("doc-1", "Quarterly report", "Revenue grew in the third quarter while support costs fell. The team expects the trend to hold."),
        new("doc-2", "Incident review", "A cache outage slowed the checkout for forty minutes. The fix was a larger connection pool."),
        new("doc-3", "Hiring plan", "Two engineers and one analyst join next month. Onboarding is handled by the platform team."),
    ];
}

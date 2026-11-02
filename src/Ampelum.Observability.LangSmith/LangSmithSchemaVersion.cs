namespace SignalGrid.Ampelum.Observability.LangSmith;

internal static class LangSmithSchemaVersion
{
    /// <summary>Snapshot date of the LangSmith /runs API contract this exporter was authored against.</summary>
    public const string SnapshotDate = "2026-03-15";

    /// <summary>Pinned LangSmith semantic-conventions version, emitted as <c>extra.runtime.langsmith_schema_version</c>.</summary>
    public const string SemanticConventionsVersion = "1.0";
}

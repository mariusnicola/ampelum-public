using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.VectorStores.PgVector;

/// <summary>Options for <see cref="PgVectorStore"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class PgVectorOptions
{
    /// <summary>Postgres connection string.</summary>
    public required string ConnectionString { get; init; }

    /// <summary>Vector dimensionality.</summary>
    public required int VectorDimension { get; init; }

    /// <summary>Storage table name. Default "ampelum_documents".</summary>
    public string TableName { get; init; } = "ampelum_documents";
}

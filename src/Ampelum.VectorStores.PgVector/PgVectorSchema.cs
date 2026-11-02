namespace SignalGrid.Ampelum.VectorStores.PgVector;

/// <summary>DDL templates for the pgvector table.</summary>
internal static class PgVectorSchema
{
    public static string CreateTable(string table, int dim) => $@"
CREATE EXTENSION IF NOT EXISTS vector;
CREATE TABLE IF NOT EXISTS {table} (
    id          TEXT PRIMARY KEY,
    text        TEXT NOT NULL,
    metadata    JSONB NOT NULL DEFAULT '{{}}'::jsonb,
    embedding   vector({dim}) NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_{table}_embedding ON {table} USING ivfflat (embedding vector_cosine_ops) WITH (lists = 100);
";
}

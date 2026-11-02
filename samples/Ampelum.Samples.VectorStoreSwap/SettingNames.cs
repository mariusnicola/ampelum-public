namespace Ampelum.Samples.VectorStoreSwap;

/// <summary>Names of the settings this sample reads. Values come from SampleConfig, never from code.</summary>
public static class SettingNames
{
    public const string VectorStore = "VECTOR_STORE";
    public const string VectorDimension = "VECTOR_DIMENSION";

    public const string PgVectorConnectionString = "PGVECTOR_CONNECTION_STRING";

    public const string PineconeApiKey = "PINECONE_API_KEY";
    public const string PineconeEnvironment = "PINECONE_ENVIRONMENT";
    public const string PineconeIndex = "PINECONE_INDEX";

    public const string QdrantHost = "QDRANT_HOST";
    public const string QdrantPort = "QDRANT_PORT";
    public const string QdrantCollection = "QDRANT_COLLECTION";

    public const string EmbeddingEndpoint = "EMBEDDING_ENDPOINT";
    public const string EmbeddingApiKey = "EMBEDDING_API_KEY";
    public const string EmbeddingModel = "EMBEDDING_MODEL";
}

/// <summary>Values accepted by the <see cref="SettingNames.VectorStore"/> setting.</summary>
public static class VectorStoreKinds
{
    public const string PgVector = "pgvector";
    public const string Pinecone = "pinecone";
    public const string Qdrant = "qdrant";
    public const string InMemory = "inmemory";

    public static IReadOnlyList<string> All { get; } = [PgVector, Pinecone, Qdrant, InMemory];

    public static string Normalize(string raw) => raw.Trim().ToLowerInvariant();
}

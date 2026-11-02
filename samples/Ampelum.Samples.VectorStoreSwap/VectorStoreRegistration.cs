using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using SignalGrid.Ampelum.DependencyInjection;
using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.VectorStores.PgVector;
using SignalGrid.Ampelum.VectorStores.Pinecone;
using SignalGrid.Ampelum.VectorStores.Qdrant;

namespace Ampelum.Samples.VectorStoreSwap;

/// <summary>
/// Backends to use instead of the real database clients. Left null, each store builds its own client
/// from its settings, which is the live path.
/// </summary>
public sealed record VectorBackends(
    IPgVectorBackend? PgVector = null,
    IPineconeIndex? Pinecone = null,
    IQdrantBackend? Qdrant = null);

public static class VectorStoreRegistration
{
    /// <summary>
    /// Registers the store named by the VECTOR_STORE setting as the one IVectorStore. Only the
    /// settings of the chosen store are read, so an unused store needs no configuration.
    /// </summary>
    public static AmpelumBuilder WithConfiguredVectorStore(
        this AmpelumBuilder builder, Func<string, string> setting, VectorBackends? backends = null)
    {
        var kind = VectorStoreKinds.Normalize(setting(SettingNames.VectorStore));
        var services = builder.Services;
        switch (kind)
        {
            case VectorStoreKinds.PgVector:
                services.AddSingleton(PgVectorOptionsFrom(setting));
                // PgVectorStore has a second constructor that takes the backend, and DI picks it
                // only when a backend is registered.
                if (backends?.PgVector is { } pg)
                    services.AddSingleton(pg);
                return builder.WithVectorStore<PgVectorStore>();

            case VectorStoreKinds.Pinecone:
                services.AddSingleton(PineconeOptionsFrom(setting));
                if (backends?.Pinecone is { } pinecone)
                    services.AddSingleton(pinecone);
                return builder.WithVectorStore<PineconeVectorStore>();

            case VectorStoreKinds.Qdrant:
                services.AddSingleton(QdrantOptionsFrom(setting));
                if (backends?.Qdrant is { } qdrant)
                    services.AddSingleton(qdrant);
                return builder.WithVectorStore<QdrantVectorStore>();

            case VectorStoreKinds.InMemory:
                return builder.WithVectorStore<InMemoryVectorStore>();

            default:
                throw new InvalidOperationException(
                    $"Unknown {SettingNames.VectorStore} '{kind}'. Use one of: {string.Join(", ", VectorStoreKinds.All)}.");
        }
    }

    public static PgVectorOptions PgVectorOptionsFrom(Func<string, string> setting) => new()
    {
        ConnectionString = setting(SettingNames.PgVectorConnectionString),
        VectorDimension = Dimension(setting),
    };

    public static PineconeOptions PineconeOptionsFrom(Func<string, string> setting) => new()
    {
        ApiKey = setting(SettingNames.PineconeApiKey),
        Environment = setting(SettingNames.PineconeEnvironment),
        IndexName = setting(SettingNames.PineconeIndex),
    };

    public static QdrantOptions QdrantOptionsFrom(Func<string, string> setting) => new()
    {
        Host = setting(SettingNames.QdrantHost),
        Port = int.Parse(setting(SettingNames.QdrantPort), CultureInfo.InvariantCulture),
        CollectionName = setting(SettingNames.QdrantCollection),
        VectorDimension = Dimension(setting),
    };

    static int Dimension(Func<string, string> setting) =>
        int.Parse(setting(SettingNames.VectorDimension), CultureInfo.InvariantCulture);
}

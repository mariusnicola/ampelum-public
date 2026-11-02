using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Testing;

namespace Ampelum.Samples.VectorStoreSwap;

/// <summary>A small support knowledge base, plus the scripted vectors used in fake mode.</summary>
public static class VectorStoreSwapData
{
    public const string CategoryKey = "category";
    public const int FakeDimension = 3;
    public const string SampleQuestion = "How long does shipping take?";

    // Ids are UUIDs because the Qdrant adapter keeps only ids it can parse as UUIDs; any other id comes
    // back as a hash and would not match the other stores.
    public const string ShippingId = "6f1c1b44-0a1e-4d62-9d1a-0b8f4f6a0001";
    public const string RefundsId = "6f1c1b44-0a1e-4d62-9d1a-0b8f4f6a0002";
    public const string InvoicesId = "6f1c1b44-0a1e-4d62-9d1a-0b8f4f6a0003";
    public const string PasswordId = "6f1c1b44-0a1e-4d62-9d1a-0b8f4f6a0004";

    // Axes: delivery, money, account.
    static readonly (string Id, string Text, string Category, float[] Vector)[] Entries =
    [
        (ShippingId, "Shipping: standard delivery takes 3 to 5 business days. Use the tracking link in the shipping email.", "logistics", [0.90f, 0.20f, 0.10f]),
        (RefundsId, "Refunds: a refund is issued to the original payment method within 5 business days after we receive the item.", "billing", [0.30f, 0.90f, 0.10f]),
        (InvoicesId, "Invoices: invoices are under Billing in the account menu and can be downloaded as PDF.", "billing", [0.10f, 0.80f, 0.30f]),
        (PasswordId, "Password reset: use the reset link on the sign in page. Support staff never ask for a password.", "account", [0.10f, 0.10f, 0.95f]),
    ];

    static readonly Dictionary<string, float[]> QueryVectors = new()
    {
        [SampleQuestion] = [0.95f, 0.25f, 0.05f],
        ["Where do I find my invoice?"] = [0.10f, 0.70f, 0.40f],
    };

    public static IReadOnlyList<RetrievedDocument> Documents { get; } =
        Entries.Select(e => new RetrievedDocument(
            e.Id, e.Text, 0, new Dictionary<string, object?> { [CategoryKey] = e.Category })).ToArray();

    /// <summary>Every text a fake run embeds has a scripted vector, so no text falls back to a zero vector.</summary>
    public static IEmbeddingGenerator<string, Embedding<float>> FakeEmbeddings() =>
        new FakeEmbeddingGenerator(
            Entries.ToDictionary(e => e.Text, e => e.Vector).Concat(QueryVectors).ToDictionary(),
            FakeDimension);
}

/// <summary>Placeholder settings for fake mode. Nothing here reaches a real service.</summary>
public static class FakeSettings
{
    /// <summary>Settings that select <paramref name="kind"/>; a key that was not provided throws like SampleConfig.Require.</summary>
    public static Func<string, string> For(string kind)
    {
        var values = new Dictionary<string, string>
        {
            [SettingNames.VectorStore] = kind,
            [SettingNames.VectorDimension] = VectorStoreSwapData.FakeDimension.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [SettingNames.PgVectorConnectionString] = "fake-connection-string",
            [SettingNames.PineconeApiKey] = "fake-api-key",
            [SettingNames.PineconeEnvironment] = "fake-environment",
            [SettingNames.PineconeIndex] = "fake-index",
            [SettingNames.QdrantHost] = "fake-host",
            [SettingNames.QdrantPort] = "1",
            [SettingNames.QdrantCollection] = "fake-collection",
        };
        return key => values.TryGetValue(key, out var value)
            ? value
            : throw new InvalidOperationException($"Missing setting {key}.");
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

public sealed class EncryptedFileSecretStoreTests : IDisposable
{
    private const string TestJti = "session-001";
    private readonly string _tempDir;
    private readonly string _filePath;
    private readonly byte[] _key;

    public EncryptedFileSecretStoreTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "EncryptedFileSecretStoreTests",
            Guid.NewGuid().ToString("N"));
        _filePath = Path.Combine(_tempDir, "secrets.json");
        _key = new byte[32]; // deterministic zero-key for testing
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task StoreThenResolve_ReturnsValue()
    {
        var store = new EncryptedFileSecretStore(_filePath, _key);
        try
        {
            await store.StoreAsync("api-key", "sk-test123", TestJti,
                TestContext.Current.CancellationToken);

            var result = await store.ResolveAsync("api-key", TestJti,
                TestContext.Current.CancellationToken);

            result.ShouldBe("sk-test123");
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public async Task Resolve_NonExistent_ThrowsSecretNotFoundException()
    {
        var store = new EncryptedFileSecretStore(_filePath, _key);
        try
        {
            await Should.ThrowAsync<SecretNotFoundException>(() =>
                store.ResolveAsync("missing-alias", TestJti,
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public async Task Delete_RemovesStoredValue()
    {
        var store = new EncryptedFileSecretStore(_filePath, _key);
        try
        {
            await store.StoreAsync("secret-key", "super-secret", TestJti,
                TestContext.Current.CancellationToken);

            await store.DeleteAsync("secret-key", TestJti,
                TestContext.Current.CancellationToken);

            (await store.ExistsAsync("secret-key", TestJti,
                TestContext.Current.CancellationToken)).ShouldBeFalse();
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public async Task ListAliases_ReturnsAllKeys()
    {
        var store = new EncryptedFileSecretStore(_filePath, _key);
        try
        {
            await store.StoreAsync("api-key", "sk-123", TestJti,
                TestContext.Current.CancellationToken);
            await store.StoreAsync("db-pass", "pwd-456", TestJti,
                TestContext.Current.CancellationToken);
            await store.StoreAsync("github-token", "ghp-789", TestJti,
                TestContext.Current.CancellationToken);

            var aliases = await store.ListAliasesAsync(TestJti,
                TestContext.Current.CancellationToken);

            aliases.ShouldBe(new[] { "api-key", "db-pass", "github-token" },
                ignoreOrder: true);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public async Task FileIsEncrypted()
    {
        const string plaintext = "my-very-secret-value";
        var store = new EncryptedFileSecretStore(_filePath, _key);
        try
        {
            await store.StoreAsync("secret-key", plaintext, TestJti,
                TestContext.Current.CancellationToken);
        }
        finally
        {
            store.Dispose();
        }

        // Raw file content must NOT contain the plaintext value
        var rawJson = await File.ReadAllTextAsync(_filePath,
            TestContext.Current.CancellationToken);

        rawJson.ShouldNotContain(plaintext);

        // The alias key is stored in plaintext (it's the lookup key, not a secret)
        rawJson.ShouldContain("secret-key");

        // The encrypted value should be base64 (no plaintext visible)
        // Verify it's valid JSON
        var doc = JsonDocument.Parse(rawJson);
        doc.RootElement.ValueKind.ShouldBe(JsonValueKind.Object);
    }
}
